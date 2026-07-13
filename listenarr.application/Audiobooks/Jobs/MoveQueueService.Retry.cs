namespace Listenarr.Application.Audiobooks.Jobs;

public partial class MoveQueueService
{
    public async Task<MoveRetryScheduleResult> ScheduleRetryAsync(
        Guid id,
        string leaseOwner,
        int leaseGeneration,
        string error,
        CancellationToken cancellationToken = default)
    {
        var job = await _persistence.GetByIdAsync(id, cancellationToken)
            ?? throw new MoveLeaseLostException(id, leaseGeneration);
        var nextAttemptCount = job.AttemptCount + 1;
        var now = _timeProvider.GetUtcNow();
        var nextAttemptAt = now.Add(
            MoveTimingPolicy.GetRetryDelay(id, nextAttemptCount));
        var result = await _persistence.ScheduleRetryAsync(
            id,
            leaseOwner,
            leaseGeneration,
            job.AttemptCount,
            now,
            nextAttemptAt,
            MoveTimingPolicy.MaxTransientAttempts,
            error,
            cancellationToken)
            ?? throw new MoveLeaseLostException(id, leaseGeneration);

        var reportedError = result.Status == MoveJobStatus.NeedsAttention
            ? $"{error} Automatic retry limit exhausted; operator attention is required."
            : error;
        await NotifyPersistedJobStateAsync(
            id,
            result.Status,
            reportedError,
            cancellationToken);
        return result;
    }
}
