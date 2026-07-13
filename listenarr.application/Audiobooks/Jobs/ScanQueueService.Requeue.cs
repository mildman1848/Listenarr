using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Jobs;

public partial class ScanQueueService
{
    public async Task<Guid?> RequeueScanAsync(Guid jobId)
    {
        ScanJob original;
        await _enqueueGate.WaitAsync();
        try
        {
            if (!_jobs.TryGetValue(jobId, out var current))
            {
                _logger.LogWarning("Attempted to requeue unknown scan job {JobId}", jobId);
                return null;
            }

            if (!CanRequeueJobStatus(current.Status))
            {
                return null;
            }

            original = Clone(current);
        }
        finally
        {
            _enqueueGate.Release();
        }

        if (original.MoveScanHandoffId.HasValue && _handoffStore != null)
        {
            var now = _timeProvider.GetUtcNow();
            if (!await _handoffStore.RequeueAsync(
                    original.MoveScanHandoffId.Value,
                    original.Id,
                    original.MoveScanAttemptGeneration,
                    error: null,
                    now))
            {
                return null;
            }

            var owner = $"manual-scan-{Environment.ProcessId}-{Guid.NewGuid():N}";
            var claim = await _handoffStore.TryClaimAsync(
                original.MoveScanHandoffId.Value,
                owner,
                now,
                now.Add(MoveHandoffLeaseDuration));
            if (claim == null)
            {
                return null;
            }

            var audiobook = new Audiobook { Id = original.AudiobookId };
            var newJobId = await EnqueueMoveHandoffScanAsync(audiobook, claim);
            if (!newJobId.HasValue)
            {
                await _handoffStore.ReleaseClaimAsync(
                    claim.HandoffId,
                    claim.LeaseOwner,
                    claim.LeaseGeneration,
                    "A different scan is already active for this audiobook.",
                    now);
            }

            return newJobId;
        }

        var replacement = new ScanJob
        {
            AudiobookId = original.AudiobookId,
            Path = original.Path,
            PathIdentity = original.PathIdentity,
            CorrelationId = original.CorrelationId,
            DownloadId = original.DownloadId
        };
        return await EnqueueJobAsync(
            replacement,
            allowUncorrelatedPathDedupe: string.IsNullOrWhiteSpace(replacement.CorrelationId));
    }

    private async Task CompleteReservationAsync(
        int audiobookId,
        DispatchReservation reservation,
        Guid? result)
    {
        await _enqueueGate.WaitAsync();
        try
        {
            if (_dispatchReservations.TryGetValue(audiobookId, out var current)
                && ReferenceEquals(current, reservation))
            {
                _dispatchReservations.Remove(audiobookId);
            }

            reservation.Completion.TrySetResult(result);
        }
        finally
        {
            _enqueueGate.Release();
        }
    }

    private ScanJob? FindCorrelatedActiveJob(ScanJob job) =>
        _jobs.Values.FirstOrDefault(candidate =>
            candidate.AudiobookId == job.AudiobookId
            && string.Equals(candidate.CorrelationId, job.CorrelationId, StringComparison.Ordinal)
            && candidate.MoveScanHandoffId == job.MoveScanHandoffId
            && candidate.MoveScanAttemptGeneration == job.MoveScanAttemptGeneration
            && IsActive(candidate.Status));

    private static bool PathsMatch(ScanJob left, ScanJob right)
    {
        if (left.Path == null || right.Path == null)
        {
            return left.Path == null && right.Path == null;
        }

        var identity = right.PathIdentity ?? left.PathIdentity;
        if (!identity.HasValue)
        {
            return string.Equals(left.Path, right.Path, StringComparison.Ordinal);
        }

        if (left.PathIdentity.HasValue
            && left.PathIdentity.Value.Syntax != identity.Value.Syntax)
        {
            return false;
        }

        return FileSystemPathIdentity.AreEquivalent(
            left.Path,
            right.Path,
            identity.Value.Semantics);
    }

    private async Task<PathIdentitySnapshot> ResolvePathIdentityAsync(string path)
    {
        var resolution = await _semanticsResolver.ResolveAsync(path);
        if (resolution.State != PathIdentityState.Valid)
        {
            throw new InvalidOperationException(
                resolution.Reason ?? "Scan filesystem identity is unavailable.");
        }

        return PathIdentitySnapshot.FromResolution(
            resolution.Semantics,
            FileSystemCaseSensitivityMode.Auto,
            resolution.BoundaryPath,
            path);
    }

    private void UpdateJobStatusCore(Guid id, string status, string? error)
    {
        if (_jobs.TryGetValue(id, out var job))
        {
            job.Status = status;
            job.Error = error;
            _jobs[id] = job;
            _logger.LogInformation("Updated scan job {JobId} status to {Status}", id, status);
        }
        else
        {
            _logger.LogWarning("Attempted to update unknown scan job {JobId} to {Status}", id, status);
        }
    }

    private static ScanJob Clone(ScanJob job) => new()
    {
        Id = job.Id,
        AudiobookId = job.AudiobookId,
        Path = job.Path,
        PathIdentity = job.PathIdentity,
        EnqueuedAt = job.EnqueuedAt,
        Status = job.Status,
        Error = job.Error,
        CorrelationId = job.CorrelationId,
        DownloadId = job.DownloadId,
        MoveScanHandoffId = job.MoveScanHandoffId,
        MoveScanAttemptGeneration = job.MoveScanAttemptGeneration
    };

    private static bool IsActive(string status) =>
        string.Equals(status, "Queued", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "Processing", StringComparison.OrdinalIgnoreCase);

    private static bool CanRequeueJobStatus(string status) =>
        string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "Queued", StringComparison.OrdinalIgnoreCase);

    private sealed record DispatchReservation(MoveScanHandoffClaim Claim)
    {
        public TaskCompletionSource<Guid?> Completion { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Matches(MoveScanHandoffClaim other) =>
            Claim.HandoffId == other.HandoffId
            && Claim.AttemptGeneration == other.AttemptGeneration
            && Claim.LeaseGeneration == other.LeaseGeneration;
    }
}
