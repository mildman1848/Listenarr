using Listenarr.Application.Common;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Jobs;

public partial class MoveQueueService
{
    public async Task<Guid?> RequeueMoveAsync(Guid jobId)
    {
        await EnsureIdentityKeysReconciledAsync(CancellationToken.None);
        MoveJob? jobToSchedule = null;
        var result = await _mutationCoordinator.ExecuteExclusiveAsync<Guid?>(async cancellationToken =>
        {
            MoveJob? job;
            try
            {
                job = await _persistence.GetByIdAsync(jobId, cancellationToken);
            }
            catch (Exception ex) when (WorkerExceptionClassifier.IsNonFatal(ex))
            {
                _logger.LogWarning(ex, "Failed to read move job from DB while requeueing {JobId}", jobId);
                return null;
            }

            if (job == null)
            {
                _logger.LogWarning("Attempted to requeue unknown move job {JobId}", jobId);
                return null;
            }

            if (!CanRequeueJobStatus(job.Status))
            {
                _logger.LogInformation("Move job {JobId} has status {Status} and cannot be requeued", jobId, job.Status);
                return null;
            }

            if (string.IsNullOrWhiteSpace(job.SourcePath)
                || string.IsNullOrWhiteSpace(job.RequestedPath))
            {
                _logger.LogWarning(
                    "Move job {JobId} has no complete source and target paths and cannot be requeued safely",
                    jobId);
                return null;
            }

            var sourcePath = job.SourcePath;
            PathIdentitySnapshot sourceIdentity;
            if (!job.TryGetSourceIdentity(out sourceIdentity))
            {
                sourcePath = FileSystemPathIdentity.ResolveNativeAbsolutePath(sourcePath);
                sourceIdentity = await ResolveIdentitySnapshotAsync(
                    sourcePath,
                    cancellationToken: cancellationToken);
            }
            else
            {
                sourcePath = FileSystemPathIdentity.Canonicalize(
                    sourcePath,
                    sourceIdentity.Syntax);
            }

            var targetPath = job.RequestedPath;
            PathIdentitySnapshot targetIdentity;
            if (!job.TryGetTargetIdentity(out targetIdentity))
            {
                targetPath = FileSystemPathIdentity.ResolveNativeAbsolutePath(targetPath);
                targetIdentity = await ResolveIdentitySnapshotAsync(
                    targetPath,
                    cancellationToken: cancellationToken);
            }
            else
            {
                targetPath = FileSystemPathIdentity.Canonicalize(
                    targetPath,
                    targetIdentity.Syntax);
            }

            await ThrowIfRelocationBoundaryProtectedAsync(
                sourcePath,
                sourceIdentity,
                targetPath,
                targetIdentity,
                cancellationToken);

            if (FileSystemPathIdentity.AreEquivalentEndpoints(
                    sourcePath,
                    sourceIdentity,
                    targetPath,
                    targetIdentity))
            {
                _logger.LogInformation(
                    "Move job {JobId} cannot be manually requeued because its source and target endpoints are identical",
                    job.Id);
                return null;
            }

            var deduplicationKey = BuildDeduplicationKey(
                job.AudiobookId,
                targetPath,
                targetIdentity);
            var requeue = await _persistence.RequeueAsync(
                new RequeueMoveCommand(
                    job.Id,
                    job.Status,
                    sourcePath,
                    sourceIdentity,
                    targetPath,
                    targetIdentity,
                    deduplicationKey,
                    _timeProvider.GetUtcNow()),
                cancellationToken);
            switch (requeue.Outcome)
            {
                case MoveRequeueOutcome.Requeued:
                case MoveRequeueOutcome.AlreadyQueuedWithMatchingIdentity:
                    jobToSchedule = requeue.Job;
                    _logger.LogInformation(
                        "Requeued move job {JobId} for audiobook {AudiobookId}",
                        jobId,
                        job.AudiobookId);
                    return requeue.Job?.Id;
                case MoveRequeueOutcome.ConflictingActiveJob:
                    jobToSchedule = requeue.Job;
                    return requeue.Job?.Id;
                case MoveRequeueOutcome.StaleState:
                    _logger.LogInformation(
                        "Move job {JobId} changed state while it was being requeued",
                        jobId);
                    return null;
                case MoveRequeueOutcome.NotFound:
                    _logger.LogWarning("Move job {JobId} disappeared while it was being requeued", jobId);
                    return null;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported move requeue outcome {requeue.Outcome}.");
            }
        });

        if (jobToSchedule != null)
        {
            await ScheduleAsync(jobToSchedule);
            await NotifyPersistedJobStateAsync(
                jobToSchedule.Id,
                jobToSchedule.Status,
                jobToSchedule.Error);
        }

        return result;
    }

    private async Task ScheduleAsync(MoveJob job, CancellationToken cancellationToken = default)
    {
        await _channel.Writer.WriteAsync(job, cancellationToken);
    }

    private static bool CanRequeueJobStatus(MoveJobStatus status) =>
        status is MoveJobStatus.Failed or
            MoveJobStatus.NeedsAttention or
            MoveJobStatus.Completed or
            MoveJobStatus.Queued;

    private static string BuildDeduplicationKey(
        int audiobookId,
        string requestedPath,
        PathIdentitySnapshot targetIdentity) =>
        FileSystemPathIdentity.CreateKey(
            $"move:{audiobookId}",
            requestedPath,
            targetIdentity.Semantics,
            version: 3);

    private async Task<PathIdentitySnapshot> ResolveIdentitySnapshotAsync(
        string path,
        FileSystemCaseSensitivityMode mode = FileSystemCaseSensitivityMode.Auto,
        CancellationToken cancellationToken = default)
    {
        var absolutePath = FileSystemPathIdentity.ResolveNativeAbsolutePath(path);
        var resolution = await _semanticsResolver.ResolveAsync(
            absolutePath,
            mode,
            cancellationToken);
        if (resolution.State != PathIdentityState.Valid)
        {
            throw new InvalidOperationException(
                resolution.Reason ?? "Filesystem identity is unavailable.");
        }

        return PathIdentitySnapshot.FromResolution(
            resolution.Semantics,
            mode,
            resolution.BoundaryPath,
            absolutePath);
    }

    private async Task EnsureIdentityKeysReconciledAsync(CancellationToken cancellationToken)
    {
        if (_identityKeysReconciled)
        {
            return;
        }

        await _identityReconciliationGate.WaitAsync(cancellationToken);
        try
        {
            if (_identityKeysReconciled)
            {
                return;
            }

            await _persistence.ReconcileIdentityKeysAsync(cancellationToken);
            _identityKeysReconciled = true;
        }
        finally
        {
            _identityReconciliationGate.Release();
        }
    }

    private async Task ThrowIfRelocationBoundaryProtectedAsync(
        string sourcePath,
        PathIdentitySnapshot sourceIdentity,
        string targetPath,
        PathIdentitySnapshot targetIdentity,
        CancellationToken cancellationToken = default)
    {
        await ThrowIfEndpointProtectedAsync(
            targetPath,
            targetIdentity,
            "target",
            cancellationToken);
        await ThrowIfEndpointProtectedAsync(
            sourcePath,
            sourceIdentity,
            "source",
            cancellationToken);
    }

    private async Task ThrowIfEndpointProtectedAsync(
        string path,
        PathIdentitySnapshot identity,
        string endpoint,
        CancellationToken cancellationToken)
    {
        identity.ValidateForPath(path);
        if (await _relocationService.IsBoundaryProtectedAsync(
            path,
            identity.Semantics,
            cancellationToken))
        {
            throw new MoveRelocationConflictException(
                $"Move {endpoint} overlaps an active root folder relocation boundary.");
        }
    }

    private async Task<bool> PersistWithRetryAsync(
        Func<Task<bool>> operation,
        CancellationToken cancellationToken)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (PersistenceException) when (attempt < maxAttempts)
            {
                await Task.Delay(
                    TimeSpan.FromMilliseconds(100 * attempt * attempt),
                    cancellationToken);
            }
        }
    }
}

public sealed class MoveRelocationConflictException(string message) : InvalidOperationException(message);
