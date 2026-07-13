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
            token => RetryCoreAsync(relocationId, token),
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
                await transaction.CommitAsync(cancellationToken);
                var fallbackPath = ResolveCurrentPathFallback(relocation);
                string? unavailableRootPath = null;
                if (relocation.RootFolderId is int unavailableRootFolderId)
                {
                    unavailableRootPath = await db.RootFolders
                        .Where(root => root.Id == unavailableRootFolderId)
                        .Select(root => root.Path)
                        .SingleOrDefaultAsync(cancellationToken);
                }

                var unavailableResult = Map(relocation, unavailableRootPath ?? fallbackPath);
                return unavailableResult;
            }
        }

        var skippedSupersededJobs = 0;
        foreach (var job in relocation.MoveJobs.Where(job => job.Status is
            MoveJobStatus.NeedsAttention or MoveJobStatus.Failed or MoveJobStatus.Superseded))
        {
            if (string.IsNullOrWhiteSpace(job.RequestedPath)
                || !job.TryGetTargetIdentity(out var targetIdentity))
            {
                job.Status = MoveJobStatus.NeedsAttention;
                job.Error = "The move job has no authoritative target filesystem identity.";
                job.FailureKind = MoveFailureKind.Verification;
                continue;
            }

            var deduplicationKey = FileSystemPathIdentity.CreateKey(
                $"move:{job.AudiobookId}",
                job.RequestedPath,
                targetIdentity.Semantics,
                version: 3);
            var conflictingJob = await db.MoveJobs.AsNoTracking().FirstOrDefaultAsync(
                candidate => candidate.Id != job.Id
                    && candidate.ActiveDeduplicationKey == deduplicationKey,
                cancellationToken);
            if (conflictingJob != null)
            {
                if (job.Status == MoveJobStatus.Superseded)
                {
                    skippedSupersededJobs++;
                    continue;
                }

                throw new ApplicationConflictException(
                    "move_job_retry_conflict",
                    "A newer move for this audiobook is already active.");
            }

            MoveJobManualRetry.Reset(job, deduplicationKey, now);
            job.IdentityKeyVersion = 3;
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
        if (remainingSkippedItems > 0 || skippedSupersededJobs > 0)
        {
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            relocation.Error = BuildRetryAttentionError(remainingSkippedItems, skippedSupersededJobs);
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
        await transaction.CommitAsync(cancellationToken);
        var resultFallbackPath = ResolveCurrentPathFallback(relocation);
        string? rootPath = null;
        if (relocation.RootFolderId is int resultRootFolderId)
        {
            rootPath = await db.RootFolders
                .Where(root => root.Id == resultRootFolderId)
                .Select(root => root.Path)
                .SingleOrDefaultAsync(cancellationToken);
        }

        var result = Map(relocation, rootPath ?? resultFallbackPath);
        return result;
    }
}
