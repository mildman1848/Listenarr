using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    IFileSystemSemanticsResolver semanticsResolver,
    IHubBroadcaster hubBroadcaster,
    TimeProvider timeProvider,
    IFilesystemMutationCoordinator mutationCoordinator,
    IAudiobookOperationCoordinator audiobookOperationCoordinator,
    IMoveSourceManifestService moveSourceManifestService) : IRootFolderRelocationService
{
    private readonly SemaphoreSlim _rootIdentityGate = new(1, 1);
    private readonly IFilesystemMutationCoordinator _mutationCoordinator =
        mutationCoordinator ?? throw new ArgumentNullException(nameof(mutationCoordinator));
    private readonly IAudiobookOperationCoordinator _audiobookOperationCoordinator =
        audiobookOperationCoordinator ?? throw new ArgumentNullException(nameof(audiobookOperationCoordinator));
    private bool _rootIdentitiesReconciled;
    public async Task<RootFolderPathChangeResult> StartAsync(
        int rootFolderId,
        RootFolderPathChangeCommand command,
        CancellationToken cancellationToken = default)
    {
        var outcome = await _mutationCoordinator.ExecuteExclusiveAsync(
            token => ExecuteWithAllAudiobookLocksAsync(
                lockedToken => StartCoreAsync(rootFolderId, command, lockedToken),
                token),
            cancellationToken);
        if (outcome.Broadcast)
        {
            await BroadcastAsync(outcome.Result, cancellationToken);
        }

        return outcome.Result;
    }

    private async Task<StartOutcome> StartCoreAsync(
        int rootFolderId,
        RootFolderPathChangeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.DesiredName))
        {
            throw new ArgumentException("Root folder name is required.", nameof(command));
        }

        RejectTargetNavigationSegments(command.TargetPath);
        var targetPath = FileUtils.NormalizeRootFolderPathForStorage(command.TargetPath);
        var targetResolution = await semanticsResolver.ResolveAsync(
            targetPath,
            command.TargetCaseSensitivityMode,
            cancellationToken);
        if (targetResolution.State != PathIdentityState.Valid)
        {
            throw new InvalidOperationException(
                targetResolution.Reason ?? "Target filesystem semantics are unavailable; select an explicit override.");
        }

        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var root = await db.RootFolders.SingleOrDefaultAsync(
            candidate => candidate.Id == rootFolderId,
            cancellationToken)
            ?? throw new KeyNotFoundException("Root folder not found");
        if (await db.RootFolderRelocations.AnyAsync(
            relocation => relocation.ActiveRootFolderId == rootFolderId,
            cancellationToken))
        {
            throw new InvalidOperationException("The root folder already has an active relocation.");
        }

        FileSystemSemanticsResolution? sourceResolution = null;
        try
        {
            var resolvedSource = await semanticsResolver.ResolveAsync(
                root.Path,
                root.CaseSensitivityMode,
                cancellationToken);
            if (resolvedSource.State == PathIdentityState.Valid)
            {
                sourceResolution = resolvedSource;
            }
        }
        catch (ArgumentException)
        {
            sourceResolution = null;
        }

        if (sourceResolution == null && command.Mode != RootFolderRelocationMode.MetadataOnly)
        {
            throw new InvalidOperationException(
                "The current root folder path is invalid or unavailable; use metadata-only path change to repair it before relocating files.");
        }

        if (sourceResolution != null && command.Mode != RootFolderRelocationMode.MetadataOnly)
        {
            var sourceRootIdentity = PathIdentitySnapshot.FromResolution(
                sourceResolution.Semantics,
                root.CaseSensitivityMode,
                sourceResolution.BoundaryPath,
                root.Path);
            var targetRootIdentity = PathIdentitySnapshot.FromResolution(
                targetResolution.Semantics,
                command.TargetCaseSensitivityMode,
                targetResolution.BoundaryPath,
                targetPath);
            if (FileSystemPathIdentity.AreEquivalentEndpoints(
                    root.Path,
                    sourceRootIdentity,
                    targetPath,
                    targetRootIdentity))
            {
                throw new ArgumentException(
                    "Root folder relocation source and target paths must be distinct.",
                    nameof(command));
            }
        }

        var storedSourcePathSemantics = sourceResolution == null
            ? ResolveStoredSourcePathSemantics(root)
            : new StoredSourcePathSemantics(sourceResolution.Semantics, false);

        var targetIdentityKey = FileSystemPathIdentity.CreateKey(
            "root",
            targetPath,
            targetResolution.Semantics);
        var otherRoots = await db.RootFolders
            .Where(candidate => candidate.Id != rootFolderId)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var activeBoundaries = await db.RootFolderRelocations
            .Where(relocation => relocation.ActiveRootFolderId != null)
            .AsNoTracking()
            .Select(relocation => new
            {
                relocation.SourcePath,
                relocation.SourceCaseSensitivityMode,
                relocation.TargetPath,
                relocation.TargetCaseSensitivityMode
            })
            .ToListAsync(cancellationToken);
        var targetConflict = otherRoots.Any(candidate =>
            RootBoundaryConflictsWithTarget(candidate, targetPath, targetIdentityKey, targetResolution.Semantics));
        foreach (var boundary in activeBoundaries)
        {
            targetConflict = targetConflict
                || await ActiveBoundaryConflictsWithTargetAsync(
                    targetPath,
                    targetResolution.Semantics,
                    boundary.SourcePath,
                    boundary.SourceCaseSensitivityMode,
                    cancellationToken)
                || await ActiveBoundaryConflictsWithTargetAsync(
                    targetPath,
                    targetResolution.Semantics,
                    boundary.TargetPath,
                    boundary.TargetCaseSensitivityMode,
                    cancellationToken);
            if (targetConflict)
            {
                break;
            }
        }
        if (targetConflict)
        {
            throw new InvalidOperationException("A root folder with that filesystem identity already exists.");
        }

        var audiobookRows = await db.Audiobooks
            .Where(audiobook => audiobook.BasePath != null)
            .Select(audiobook => new
            {
                Audiobook = audiobook,
                StoredBasePath = EF.Property<string>(audiobook, nameof(Audiobook.BasePath))!
            })
            .ToListAsync(cancellationToken);
        var audiobookIds = audiobookRows.Select(row => row.Audiobook.Id).ToList();
        await db.AudiobookFiles
            .Where(file => audiobookIds.Contains(file.AudiobookId))
            .LoadAsync(cancellationToken);
        var audiobooks = audiobookRows
            .Select(row => new AudiobookPathCandidate(row.Audiobook, row.StoredBasePath))
            .ToList();
        var (affected, invalidStoredBasePaths) = storedSourcePathSemantics == null
            ? (new List<AudiobookPathCandidate>(), new List<AudiobookPathCandidate>())
            : DiscoverAffectedAudiobooks(
                audiobooks,
                root.Path,
                storedSourcePathSemantics.Semantics,
                storedSourcePathSemantics.DetectAmbiguousCaseMatches);

        if (command.Mode != RootFolderRelocationMode.MetadataOnly && invalidStoredBasePaths.Count > 0)
        {
            throw new InvalidOperationException(
                "One or more audiobook base paths are invalid; use metadata-only path change to repair stored metadata before relocating files.");
        }

        var affectedAudiobookIds = affected.Select(candidate => candidate.Audiobook.Id).ToHashSet();
        var activeMoveJobs = await db.MoveJobs
            .Where(job => job.Status == MoveJobStatus.Queued
                || job.Status == MoveJobStatus.Running
                || job.Status == MoveJobStatus.RetryScheduled)
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var conflictingMoveJob = activeMoveJobs.FirstOrDefault(job =>
            affectedAudiobookIds.Contains(job.AudiobookId)
            || (sourceResolution != null
                && (PathTouchesBoundary(job.SourcePath, root.Path, sourceResolution.Semantics)
                    || PathTouchesBoundary(job.RequestedPath, root.Path, sourceResolution.Semantics)))
            || PathTouchesBoundary(job.SourcePath, targetPath, targetResolution.Semantics)
            || PathTouchesBoundary(job.RequestedPath, targetPath, targetResolution.Semantics));
        if (conflictingMoveJob != null)
        {
            throw new InvalidOperationException(
                $"Active move job {conflictingMoveJob.Id} overlaps this root folder relocation; wait for it to finish before starting the relocation.");
        }

        var movePlans = new List<RelocationMovePlan>();
        if (sourceResolution != null
            && command.Mode == RootFolderRelocationMode.Relocate)
        {
            foreach (var candidate in affected)
            {
                var manifest = await moveSourceManifestService.BuildAsync(
                    candidate.Audiobook,
                    cancellationToken);
                if (!FileSystemPathIdentity.IsSameOrInside(
                        manifest.SourceRoot,
                        root.Path,
                        sourceResolution.Semantics))
                {
                    throw new InvalidOperationException(
                        "A tracked audiobook move source escaped the relocating root folder.");
                }

                var requestedPath = MapTargetPath(
                    root.Path,
                    targetPath,
                    manifest.SourceRoot,
                    sourceResolution.Semantics,
                    targetResolution.Semantics);
                var targetIdentity = PathIdentitySnapshot.FromResolution(
                    targetResolution.Semantics,
                    command.TargetCaseSensitivityMode,
                    targetPath,
                    requestedPath);
                if (FileSystemPathIdentity.AreEquivalentEndpoints(
                        manifest.SourceRoot,
                        manifest.SourceIdentity,
                        requestedPath,
                        targetIdentity))
                {
                    throw new InvalidOperationException(
                        "Root folder relocation produced an identical source and target child move.");
                }

                movePlans.Add(new RelocationMovePlan(
                    candidate,
                    manifest,
                    requestedPath,
                    targetIdentity));
            }

            RejectDuplicateRelocationTargets(movePlans, targetResolution.Semantics);
        }

        var now = timeProvider.GetUtcNow();
        var nowUtc = now.UtcDateTime;

        if (command.Mode == RootFolderRelocationMode.MetadataOnly)
        {
            var sourcePath = root.Path;
            var sourceCaseSensitivityMode = root.CaseSensitivityMode;
            var skipped = invalidStoredBasePaths
                .Select(candidate => new RootFolderRelocationSkippedItem
                {
                    AudiobookId = candidate.Audiobook.Id,
                    Reason = "Stored audiobook base path is invalid or case-ambiguous and could not be compared safely with the source root.",
                    CreatedAt = now
                })
                .ToList();
            var metadataTotal = affected.Count + skipped.Count;
            var completed = 0;

            var metadataSourceSemantics = storedSourcePathSemantics?.Semantics;
            foreach (var candidate in affected)
            {
                var audiobook = candidate.Audiobook;
                var sourceSemantics = metadataSourceSemantics
                    ?? throw new InvalidOperationException("Stored source path semantics are unavailable.");
                var sourceBasePath = candidate.StoredBasePath;
                try
                {
                    var destinationBasePath = MapTargetPath(
                        sourcePath,
                        targetPath,
                        sourceBasePath,
                        sourceSemantics,
                        targetResolution.Semantics);
                    AudiobookPathReferenceRewriter.Rewrite(
                        audiobook,
                        sourceBasePath,
                        destinationBasePath,
                        sourceSemantics,
                        targetResolution.Semantics,
                        command.TargetCaseSensitivityMode);
                    completed++;
                }
                catch (InvalidOperationException ex)
                {
                    skipped.Add(new RootFolderRelocationSkippedItem
                    {
                        AudiobookId = audiobook.Id,
                        Reason = ex.Message,
                        CreatedAt = now
                    });
                }
            }

            RejectDuplicateAudiobookFileOwnership(db);
            ApplyRootMetadata(root, command, targetPath, targetResolution, targetIdentityKey);
            if (command.DesiredIsDefault)
            {
                await ClearOtherDefaultsAsync(db, rootFolderId, cancellationToken);
            }

            RootFolderRelocation? metadataRelocation = null;
            if (skipped.Count > 0)
            {
                metadataRelocation = new RootFolderRelocation
                {
                    RootFolderId = root.Id,
                    ActiveRootFolderId = root.Id,
                    SourcePath = sourcePath,
                    SourceCaseSensitivityMode = sourceCaseSensitivityMode,
                    TargetPath = targetPath,
                    Mode = command.Mode,
                    Status = RootFolderRelocationStatus.NeedsAttention,
                    DeleteEmptySource = command.DeleteEmptySource,
                    DesiredName = command.DesiredName.Trim(),
                    DesiredIsDefault = command.DesiredIsDefault,
                    TargetCaseSensitivityMode = command.TargetCaseSensitivityMode,
                    TotalJobs = metadataTotal,
                    CompletedJobs = completed,
                    Error = BuildSkippedMetadataError(skipped.Count),
                    CreatedAt = nowUtc,
                    UpdatedAt = nowUtc
                };
                foreach (var skippedItem in skipped)
                {
                    metadataRelocation.SkippedItems.Add(skippedItem);
                }

                db.RootFolderRelocations.Add(metadataRelocation);
            }

            await db.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(CancellationToken.None);
            var metadataResult = new RootFolderPathChangeResult(
                metadataRelocation?.Id,
                root.Id,
                root.Path,
                targetPath,
                metadataRelocation?.Status ?? RootFolderRelocationStatus.Completed,
                metadataTotal,
                completed,
                metadataRelocation?.Error);
            return new StartOutcome(metadataResult, metadataRelocation != null);
        }

        var relocation = new RootFolderRelocation
        {
            RootFolderId = root.Id,
            ActiveRootFolderId = root.Id,
            SourcePath = root.Path,
            SourceCaseSensitivityMode = root.CaseSensitivityMode,
            TargetPath = targetPath,
            Mode = command.Mode,
            Status = RootFolderRelocationStatus.Pending,
            DeleteEmptySource = command.DeleteEmptySource,
            DesiredName = command.DesiredName.Trim(),
            DesiredIsDefault = command.DesiredIsDefault,
            TargetCaseSensitivityMode = command.TargetCaseSensitivityMode,
            TotalJobs = movePlans.Count,
            CreatedAt = nowUtc
        };
        db.RootFolderRelocations.Add(relocation);

        foreach (var plan in movePlans)
        {
            var audiobook = plan.Candidate.Audiobook;
            var entries = plan.Manifest.Entries
                .Select(entry => new MoveJobEntry
                {
                    RelativePath = entry.RelativePath,
                    EntryType = entry.EntryType,
                    Length = entry.Length,
                    LastWriteTimeUtc = entry.LastWriteTimeUtc,
                    Sha256 = entry.Sha256,
                    CopyState = MoveJobEntryCopyState.Pending,
                    CleanupState = MoveJobEntryCleanupState.Pending
                })
                .ToList();
            var moveJob = new MoveJob
            {
                AudiobookId = audiobook.Id,
                RequestedPath = plan.RequestedPath,
                SourcePath = plan.Manifest.SourceRoot,
                SourceCleanupBoundary = root.Path,
                DeleteEmptySource = command.DeleteEmptySource,
                Status = MoveJobStatus.Queued,
                Phase = MoveJobPhase.None,
                EnqueuedAt = nowUtc,
                RelocationId = relocation.Id,
                IdentityKeyVersion = MoveManifestIdentity.Version,
                ActiveDeduplicationKey = MoveManifestIdentity.CreateDeduplicationKey(
                    audiobook.Id,
                    plan.Manifest.SourceRoot,
                    plan.Manifest.SourceIdentity,
                    plan.RequestedPath,
                    plan.TargetIdentity,
                    entries),
                Entries = entries
            };
            moveJob.SetSourceIdentity(plan.Manifest.SourceIdentity);
            moveJob.SetTargetIdentity(plan.TargetIdentity);
            db.MoveJobs.Add(moveJob);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (affected.Count == 0)
        {
            ApplyRootMetadata(root, command, targetPath, targetResolution, targetIdentityKey);
            relocation.Status = RootFolderRelocationStatus.Completed;
            relocation.ActiveRootFolderId = null;
            relocation.CompletedAt = nowUtc;
            await db.SaveChangesAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await transaction.CommitAsync(CancellationToken.None);
        var result = Map(relocation, root.Path);
        return new StartOutcome(result, true);
    }

    private static void RejectTargetNavigationSegments(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        var root = Path.GetPathRoot(targetPath);
        var relativePath = string.IsNullOrEmpty(root)
            ? targetPath
            : targetPath[root.Length..];
        var segments = relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == "."))
        {
            throw new ArgumentException(
                "Root folder target path cannot contain current directory segments.",
                nameof(targetPath));
        }

        if (segments.Any(segment => segment == ".."))
        {
            throw new ArgumentException(
                "Root folder target path cannot contain parent traversal segments.",
                nameof(targetPath));
        }
    }

    private sealed record StartOutcome(RootFolderPathChangeResult Result, bool Broadcast);
}
