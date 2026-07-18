using Listenarr.Application.Common.Exceptions;
using Listenarr.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    public async Task<RootFolderPathChangeResult> RetryAsync(
        Guid relocationId,
        CancellationToken cancellationToken = default)
    {
        var result = await _mutationCoordinator.ExecuteExclusiveAsync(
            token => ExecuteWithAllAudiobookLocksAsync(
                lockedToken => RetryCoreAsync(relocationId, lockedToken),
                token),
            cancellationToken);
        await BroadcastAsync(result, cancellationToken);
        return result;
    }

    private async Task<RootFolderPathChangeResult> RetryCoreAsync(
        Guid relocationId,
        CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var relocation = await db.RootFolderRelocations
            .Include(candidate => candidate.MoveJobs)
                .ThenInclude(job => job.Entries)
            .Include(candidate => candidate.SkippedItems)
            .SingleOrDefaultAsync(candidate => candidate.Id == relocationId, cancellationToken)
            ?? throw new KeyNotFoundException("Root folder relocation not found");
        if (relocation.Status != RootFolderRelocationStatus.NeedsAttention)
        {
            throw new InvalidOperationException("Only relocations needing attention can be retried.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var needsTargetSemantics = relocation.SkippedItems.Count > 0;
        FileSystemSemanticsResolution? targetResolution = null;
        if (needsTargetSemantics)
        {
            targetResolution = await semanticsResolver.ResolveAsync(
                relocation.TargetPath,
                relocation.TargetCaseSensitivityMode,
                cancellationToken);
            if (targetResolution.State != PathIdentityState.Valid)
            {
                relocation.Status = RootFolderRelocationStatus.NeedsAttention;
                relocation.Error = targetResolution.Reason ?? "Target filesystem identity is unavailable.";
                relocation.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await transaction.CommitAsync(CancellationToken.None);
                var fallbackPath = ResolveCurrentPathFallback(relocation);
                string? unavailableRootPath = null;
                if (relocation.RootFolderId is int unavailableRootFolderId)
                {
                    unavailableRootPath = await db.RootFolders
                        .Where(root => root.Id == unavailableRootFolderId)
                        .Select(root => root.Path)
                        .SingleOrDefaultAsync(CancellationToken.None);
                }

                var unavailableResult = Map(relocation, unavailableRootPath ?? fallbackPath);
                return unavailableResult;
            }
        }

        var skippedSupersededJobs = 0;
        var unsafeRetryJobs = 0;
        foreach (var job in relocation.MoveJobs.Where(job => job.Status is
            MoveJobStatus.NeedsAttention or MoveJobStatus.Failed or MoveJobStatus.Superseded))
        {
            // Superseded jobs are terminal evidence that their persisted source snapshot is
            // stale. Retrying them would reactivate the exact unsafe operation that superseded
            // status is intended to fence off.
            if (job.Status == MoveJobStatus.Superseded)
            {
                skippedSupersededJobs++;
                continue;
            }

            string? sourceIdentityError = null;
            if (string.IsNullOrWhiteSpace(job.SourcePath)
                || !job.TryGetSourceIdentity(out var sourceIdentity)
                || !TryValidateRetryIdentity(
                    sourceIdentity,
                    job.SourcePath,
                    out sourceIdentityError))
            {
                job.Status = MoveJobStatus.NeedsAttention;
                job.Error = sourceIdentityError
                    ?? "The move job has no authoritative source filesystem identity.";
                job.FailureKind = MoveFailureKind.Verification;
                job.ActiveDeduplicationKey = null;
                unsafeRetryJobs++;
                continue;
            }

            string? targetIdentityError = null;
            if (string.IsNullOrWhiteSpace(job.RequestedPath)
                || !job.TryGetTargetIdentity(out var targetIdentity)
                || !TryValidateRetryIdentity(
                    targetIdentity,
                    job.RequestedPath,
                    out targetIdentityError))
            {
                job.Status = MoveJobStatus.NeedsAttention;
                job.Error = targetIdentityError
                    ?? "The move job has no authoritative target filesystem identity.";
                job.FailureKind = MoveFailureKind.Verification;
                job.ActiveDeduplicationKey = null;
                unsafeRetryJobs++;
                continue;
            }

            if (job.Entries.Count == 0
                || job.Entries.All(entry => entry.EntryType != MoveJobEntryType.File))
            {
                job.Status = MoveJobStatus.NeedsAttention;
                job.Error = "The move job has no persisted tracked-file source manifest and cannot be retried safely.";
                job.FailureKind = MoveFailureKind.Verification;
                job.ActiveDeduplicationKey = null;
                unsafeRetryJobs++;
                continue;
            }

            var deduplicationKey = MoveManifestIdentity.CreateDeduplicationKey(
                job.AudiobookId,
                job.SourcePath,
                sourceIdentity,
                job.RequestedPath,
                targetIdentity,
                job.Entries);
            var conflictingJob = await db.MoveJobs.AsNoTracking().FirstOrDefaultAsync(
                candidate => candidate.Id != job.Id
                    && candidate.ActiveDeduplicationKey == deduplicationKey,
                cancellationToken);
            if (conflictingJob != null)
            {
                throw new ApplicationConflictException(
                    "move_job_retry_conflict",
                    "A newer move for this audiobook is already active.");
            }

            MoveJobManualRetry.Reset(job, deduplicationKey, now);
            job.IdentityKeyVersion = MoveManifestIdentity.Version;
        }

        if (relocation.SkippedItems.Count > 0)
        {
            await RetrySkippedMetadataReferencesAsync(
                db,
                relocation,
                targetResolution!.Semantics,
                cancellationToken);
        }

        var remainingSkippedItems = relocation.SkippedItems.Count;
        if (remainingSkippedItems > 0
            || skippedSupersededJobs > 0
            || unsafeRetryJobs > 0)
        {
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            var retryError = BuildRetryAttentionError(
                remainingSkippedItems,
                skippedSupersededJobs);
            var unsafeError = unsafeRetryJobs > 0
                ? $"{unsafeRetryJobs} job(s) lacked authoritative source identity or tracked-file manifest evidence and were not retried."
                : string.Empty;
            relocation.Error = string.Join(
                ' ',
                new[] { retryError, unsafeError }
                    .Where(message => !string.IsNullOrWhiteSpace(message)));
        }
        else if (relocation.MoveJobs.Count == 0)
        {
            relocation.Status = RootFolderRelocationStatus.Completed;
            relocation.ActiveRootFolderId = null;
            relocation.CompletedJobs = relocation.TotalJobs;
            relocation.CompletedAt = now;
            relocation.Error = null;
        }
        else if (relocation.MoveJobs.All(job => job.Status == MoveJobStatus.Completed))
        {
            if (relocation.RootFolderId is not int rootFolderId)
            {
                throw new InvalidOperationException(
                    "The root folder no longer exists; this relocation cannot be retried.");
            }

            var root = await db.RootFolders.SingleOrDefaultAsync(
                candidate => candidate.Id == rootFolderId,
                cancellationToken)
                ?? throw new InvalidOperationException(
                    "The root folder no longer exists; this relocation cannot be retried.");
            await FinalizeCompletedRelocationAsync(
                db,
                relocation,
                root,
                now,
                cancellationToken);
            relocation.CompletedJobs = relocation.TotalJobs;
        }
        else
        {
            relocation.Status = RootFolderRelocationStatus.Running;
            relocation.Error = null;
        }

        relocation.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        var resultFallbackPath = ResolveCurrentPathFallback(relocation);
        string? rootPath = null;
        if (relocation.RootFolderId is int resultRootFolderId)
        {
            rootPath = await db.RootFolders
                .Where(root => root.Id == resultRootFolderId)
                .Select(root => root.Path)
                .SingleOrDefaultAsync(CancellationToken.None);
        }

        var result = Map(relocation, rootPath ?? resultFallbackPath);
        return result;
    }

    private static bool TryValidateRetryIdentity(
        PathIdentitySnapshot identity,
        string path,
        out string? error)
    {
        try
        {
            identity.ValidateForPath(path);
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException
            or NotSupportedException or PathTooLongException
            or System.Security.SecurityException)
        {
            error = $"The move job has an invalid persisted filesystem identity: {exception.Message}";
            return false;
        }
    }
}
