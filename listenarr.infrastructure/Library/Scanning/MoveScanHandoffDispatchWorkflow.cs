using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning;

internal enum MoveScanDispatchOutcome
{
    NotClaimed,
    Dispatched,
    Deferred,
    Failed
}

internal sealed record MoveScanDispatchResult(
    MoveScanDispatchOutcome Outcome,
    Guid? ScanJobId = null);

internal static class MoveScanHandoffDispatchWorkflow
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    public static async Task<MoveScanDispatchResult> TryDispatchPendingAsync(
        Guid handoffId,
        string ownerPrefix,
        Audiobook? knownAudiobook,
        Action<MoveScanHandoffClaim>? beforeEnqueue,
        IScanQueueService scanQueueService,
        IMoveScanHandoffStore handoffStore,
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var owner = $"{ownerPrefix}-{Environment.ProcessId}-{Guid.NewGuid():N}";
        var claim = await handoffStore.TryClaimAsync(
            handoffId,
            owner,
            now,
            now.Add(LeaseDuration),
            cancellationToken);
        if (claim == null)
        {
            return new MoveScanDispatchResult(MoveScanDispatchOutcome.NotClaimed);
        }

        try
        {
            var audiobook = knownAudiobook;
            if (audiobook == null)
            {
                using var scope = scopeFactory.CreateScope();
                var audiobookRepository = scope.ServiceProvider
                    .GetRequiredService<IAudiobookRepository>();
                audiobook = await audiobookRepository.GetByIdAsync(claim.AudiobookId);
            }

            if (audiobook == null)
            {
                await handoffStore.CompleteAttemptAsync(
                    claim.HandoffId,
                    claim.AttemptGeneration,
                    scanJobId: null,
                    MoveScanTerminalOutcome.Failed,
                    $"Audiobook {claim.AudiobookId} no longer exists.",
                    found: 0,
                    created: 0,
                    scanPath: claim.TargetPath,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                return new MoveScanDispatchResult(MoveScanDispatchOutcome.Failed);
            }

            beforeEnqueue?.Invoke(claim);
            var scanJobId = await scanQueueService.EnqueueMoveHandoffScanAsync(
                audiobook,
                claim);
            if (!scanJobId.HasValue)
            {
                await handoffStore.ReleaseClaimAsync(
                    claim.HandoffId,
                    claim.LeaseOwner,
                    claim.LeaseGeneration,
                    "A scan for this audiobook is already active; the move scan handoff was deferred.",
                    timeProvider.GetUtcNow(),
                    cancellationToken);
                return new MoveScanDispatchResult(MoveScanDispatchOutcome.Deferred);
            }

            logger.LogInformation(
                "Dispatched move scan handoff {HandoffId} attempt {AttemptGeneration} as scan job {ScanJobId}",
                claim.HandoffId,
                claim.AttemptGeneration,
                scanJobId.Value);
            return new MoveScanDispatchResult(
                MoveScanDispatchOutcome.Dispatched,
                scanJobId.Value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            logger.LogWarning(
                exception,
                "Failed to dispatch move scan handoff {HandoffId}; recovery will retry it",
                claim.HandoffId);
            try
            {
                await handoffStore.ReleaseClaimAsync(
                    claim.HandoffId,
                    claim.LeaseOwner,
                    claim.LeaseGeneration,
                    exception.Message,
                    timeProvider.GetUtcNow(),
                    cancellationToken);
            }
            catch (Exception releaseException) when (WorkerExceptionClassifier.IsNonFatal(releaseException))
            {
                logger.LogDebug(
                    releaseException,
                    "Unable to release move scan handoff {HandoffId}; waiting for lease expiry",
                    claim.HandoffId);
            }

            return new MoveScanDispatchResult(MoveScanDispatchOutcome.Failed);
        }
    }
}
