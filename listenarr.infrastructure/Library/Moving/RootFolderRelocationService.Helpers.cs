using Listenarr.Domain.Common;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed partial class RootFolderRelocationService
{
    private static string MapTargetPath(
        string sourceRoot,
        string targetRoot,
        string sourcePath,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics)
    {
        if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
            sourceRoot,
            sourcePath,
            sourceSemantics,
            out var relativePath))
        {
            throw new InvalidOperationException("An audiobook path escaped its configured root.");
        }

        if (relativePath.Length == 0)
        {
            return targetRoot;
        }

        if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
            targetRoot,
            FileSystemPathIdentity.ConvertRelativePathSyntax(
                relativePath,
                sourceSemantics.Syntax,
                targetSemantics.Syntax),
            targetSemantics,
            out var targetPath))
        {
            throw new InvalidOperationException("An audiobook path is invalid for the target root.");
        }

        return targetPath;
    }

    private sealed record AudiobookPathCandidate(Audiobook Audiobook, string StoredBasePath);

    private sealed record RelocationMovePlan(
        AudiobookPathCandidate Candidate,
        MoveSourceManifest Manifest,
        string RequestedPath,
        PathIdentitySnapshot TargetIdentity);

    private static bool IsStoredWindowsAbsolutePath(string path) =>
        (path.Length >= 3
            && char.IsAsciiLetter(path[0])
            && path[1] == ':'
            && path[2] is '\\' or '/')
        || path.StartsWith(@"\\", StringComparison.Ordinal);

    private static (
        List<AudiobookPathCandidate> Affected,
        List<AudiobookPathCandidate> InvalidStoredBasePaths) DiscoverAffectedAudiobooks(
        IEnumerable<AudiobookPathCandidate> audiobooks,
        string sourceRootPath,
        FileSystemPathSemantics sourceSemantics,
        bool detectAmbiguousCaseMatches)
    {
        var affected = new List<AudiobookPathCandidate>();
        var invalidStoredBasePaths = new List<AudiobookPathCandidate>();

        foreach (var audiobook in audiobooks)
        {
            var usesWindowsSyntax = IsStoredWindowsAbsolutePath(audiobook.StoredBasePath);
            var usesUnixSyntax = audiobook.StoredBasePath.StartsWith("/", StringComparison.Ordinal);
            if ((sourceSemantics.Syntax == FileSystemPathSyntax.Windows && usesUnixSyntax)
                || (sourceSemantics.Syntax == FileSystemPathSyntax.Unix && usesWindowsSyntax))
            {
                continue;
            }

            if (!usesWindowsSyntax && !usesUnixSyntax)
            {
                invalidStoredBasePaths.Add(audiobook);
                continue;
            }

            try
            {
                if (FileSystemPathIdentity.IsSameOrInside(
                    audiobook.StoredBasePath,
                    sourceRootPath,
                    sourceSemantics))
                {
                    affected.Add(audiobook);
                    continue;
                }

                if (detectAmbiguousCaseMatches
                    && FileSystemPathIdentity.IsSameOrInside(
                        audiobook.StoredBasePath,
                        sourceRootPath,
                        new FileSystemPathSemantics(
                            sourceSemantics.Syntax,
                            FileSystemCaseSensitivity.Insensitive)))
                {
                    invalidStoredBasePaths.Add(audiobook);
                }
            }
            catch (ArgumentException)
            {
                invalidStoredBasePaths.Add(audiobook);
            }
        }

        return (affected, invalidStoredBasePaths);
    }

    private static bool PathTouchesBoundary(
        string? path,
        string boundaryPath,
        FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            return FileSystemPathIdentity.IsSameOrInside(path, boundaryPath, semantics)
                || FileSystemPathIdentity.IsSameOrInside(boundaryPath, path, semantics);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool RootBoundaryConflictsWithTarget(
        RootFolder candidate,
        string targetPath,
        string targetIdentityKey,
        FileSystemPathSemantics targetSemantics)
    {
        var candidateSemantics = FileSystemPathIdentity.ResolveComparisonSemantics(
            candidate.ResolvedCaseSensitivity,
            targetSemantics);
        try
        {
            return candidate.PathIdentityKey == targetIdentityKey
                || FileSystemPathIdentity.EvaluateBoundaryConflict(
                    targetPath,
                    targetSemantics,
                    candidate.Path,
                    candidateSemantics) != FileSystemPathBoundaryConflict.None;
        }
        catch (ArgumentException)
        {
            return candidate.PathIdentityKey == targetIdentityKey;
        }
    }

    private async Task<bool> ActiveBoundaryConflictsWithTargetAsync(
        string targetPath,
        FileSystemPathSemantics targetSemantics,
        string boundaryPath,
        FileSystemCaseSensitivityMode boundaryMode,
        CancellationToken cancellationToken)
    {
        try
        {
            if (FileSystemPathIdentity.EvaluateBoundaryConflict(
                    targetPath,
                    targetSemantics,
                    boundaryPath,
                    targetSemantics) != FileSystemPathBoundaryConflict.None)
            {
                return true;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }

        FileSystemSemanticsResolution boundaryResolution;
        try
        {
            boundaryResolution = await semanticsResolver.ResolveAsync(
                boundaryPath,
                boundaryMode,
                cancellationToken);
        }
        catch (ArgumentException)
        {
            return false;
        }
        if (boundaryResolution.State == PathIdentityState.Valid)
        {
            return FileSystemPathIdentity.EvaluateBoundaryConflict(
                targetPath,
                targetSemantics,
                boundaryPath,
                boundaryResolution.Semantics) != FileSystemPathBoundaryConflict.None;
        }

        // If an in-flight relocation boundary cannot be resolved, over-block
        // case-only overlaps rather than allowing a second relocation to race it.
        var insensitiveTargetSemantics = new FileSystemPathSemantics(
            targetSemantics.Syntax,
            FileSystemCaseSensitivity.Insensitive);
        return FileSystemPathIdentity.EvaluateBoundaryConflict(
            targetPath,
            insensitiveTargetSemantics,
            boundaryPath,
            insensitiveTargetSemantics) != FileSystemPathBoundaryConflict.None;
    }

    private async Task FinalizeCompletedRelocationAsync(
        ListenArrDbContext db,
        RootFolderRelocation relocation,
        RootFolder root,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var resolution = await semanticsResolver.ResolveAsync(
            relocation.TargetPath,
            relocation.TargetCaseSensitivityMode,
            cancellationToken);
        if (resolution.State != PathIdentityState.Valid)
        {
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            relocation.Error = "Target filesystem identity became unavailable during finalization.";
            return;
        }
        var currentObjectIdentity =
            await ResolveExistingDirectoryObjectIdentityAsync(
                relocation.TargetPath,
                cancellationToken);
        if (!relocation.TargetDirectoryObjectIdentityVersion.HasValue
            || !currentObjectIdentity.IsAvailable
                || currentObjectIdentity.Version
                    != relocation.TargetDirectoryObjectIdentityVersion
                || !string.Equals(
                    currentObjectIdentity.Value,
                    relocation.TargetDirectoryObjectIdentity,
                    StringComparison.Ordinal))
        {
            relocation.Status = RootFolderRelocationStatus.NeedsAttention;
            relocation.Error =
                "The target directory changed after the path change was authorized.";
            return;
        }

        var command = new RootFolderPathChangeCommand(
            relocation.TargetPath,
            relocation.Mode,
            relocation.DeleteEmptySource,
            relocation.DesiredName,
            relocation.DesiredIsDefault,
            relocation.TargetCaseSensitivityMode);
        ApplyRootMetadata(
            root,
            command,
            relocation.TargetPath,
            resolution,
            FileSystemPathIdentity.CreateKey("root", relocation.TargetPath, resolution.Semantics));
        root.DirectoryObjectIdentityVersion = currentObjectIdentity.Version;
        root.DirectoryObjectIdentity = currentObjectIdentity.Value;
        root.DirectoryObjectIdentityUnavailableReason =
            currentObjectIdentity.UnavailableReason;
        if (relocation.DesiredIsDefault)
        {
            await ClearOtherDefaultsAsync(db, root.Id, cancellationToken);
        }

        await FinalizeRelocationTargetReservationsAsync(
            db,
            relocation.Id,
            cancellationToken);
        relocation.TargetIdentityEnrollmentState =
            TargetIdentityEnrollmentState.NotRequired;
        relocation.Status = RootFolderRelocationStatus.Completed;
        relocation.ActiveRootFolderId = null;
        relocation.CompletedAt = now;
        relocation.Error = null;
    }

    private static void ApplyRootMetadata(
        RootFolder root,
        RootFolderPathChangeCommand command,
        string targetPath,
        FileSystemSemanticsResolution resolution,
        string identityKey)
    {
        root.Path = targetPath;
        root.Name = command.DesiredName.Trim();
        root.IsDefault = command.DesiredIsDefault;
        root.CaseSensitivityMode = command.TargetCaseSensitivityMode;
        root.ResolvedCaseSensitivity = resolution.Semantics.CaseSensitivity;
        root.PathIdentityState = resolution.State;
        root.PathIdentityKey = identityKey;
        root.UpdatedAt = DateTime.UtcNow;
    }

    private static Task ClearOtherDefaultsAsync(
        ListenArrDbContext db,
        int rootFolderId,
        CancellationToken cancellationToken) =>
        db.RootFolders
            .Where(root => root.Id != rootFolderId && root.IsDefault)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(root => root.IsDefault, false),
                cancellationToken);

    private async Task RetrySkippedMetadataReferencesAsync(
        ListenArrDbContext db,
        RootFolderRelocation relocation,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken)
    {
        var sourceResolution = await semanticsResolver.ResolveAsync(
            relocation.SourcePath,
            relocation.SourceCaseSensitivityMode,
            cancellationToken);
        if (sourceResolution.State != PathIdentityState.Valid)
        {
            var reason = sourceResolution.Reason
                ?? "Source filesystem identity is unavailable.";
            foreach (var skippedItem in relocation.SkippedItems)
            {
                skippedItem.Reason = reason;
            }

            return;
        }

        var skippedItems = relocation.SkippedItems.ToList();
        var audiobookIds = skippedItems.Select(item => item.AudiobookId).ToList();
        var audiobooks = await db.Audiobooks
            .Include(audiobook => audiobook.Files)
            .Where(audiobook => audiobookIds.Contains(audiobook.Id))
            .ToDictionaryAsync(audiobook => audiobook.Id, cancellationToken);

        var resolvedCount = 0;
        foreach (var skippedItem in skippedItems)
        {
            if (!audiobooks.TryGetValue(skippedItem.AudiobookId, out var audiobook))
            {
                relocation.SkippedItems.Remove(skippedItem);
                db.RootFolderRelocationSkippedItems.Remove(skippedItem);
                resolvedCount++;
                continue;
            }

            if (string.IsNullOrWhiteSpace(audiobook.BasePath))
            {
                skippedItem.Reason = "Audiobook no longer has a base path to rewrite.";
                continue;
            }

            try
            {
                var sourceBasePath = audiobook.BasePath!;
                var destinationBasePath = MapTargetPath(
                    relocation.SourcePath,
                    relocation.TargetPath,
                    sourceBasePath,
                    sourceResolution.Semantics,
                    targetSemantics);
                AudiobookPathReferenceRewriter.Rewrite(
                    audiobook,
                    sourceBasePath,
                    destinationBasePath,
                    sourceResolution.Semantics,
                    targetSemantics,
                    relocation.TargetCaseSensitivityMode);
                relocation.SkippedItems.Remove(skippedItem);
                db.RootFolderRelocationSkippedItems.Remove(skippedItem);
                resolvedCount++;
            }
            catch (InvalidOperationException ex)
            {
                skippedItem.Reason = ex.Message;
            }
        }

        relocation.CompletedJobs = Math.Min(
            relocation.TotalJobs,
            relocation.CompletedJobs + resolvedCount);
    }

    private static string BuildSkippedMetadataError(int skippedCount) =>
        $"{skippedCount} audiobook(s) could not have stored paths rewritten automatically.";

    private static string BuildRetryAttentionError(int skippedCount, int supersededJobCount)
    {
        var messages = new List<string>();
        if (skippedCount > 0)
        {
            messages.Add(BuildSkippedMetadataError(skippedCount));
        }

        if (supersededJobCount > 0)
        {
            messages.Add($"{supersededJobCount} job(s) were superseded by a newer move and were not retried.");
        }

        return string.Join(" ", messages);
    }

    private static string ResolveCurrentPathFallback(RootFolderRelocation relocation) =>
        relocation.Status == RootFolderRelocationStatus.Completed
            ? relocation.TargetPath
            : relocation.SourcePath;

    private static RootFolderPathChangeResult Map(RootFolderRelocation relocation, string currentPath) => new(
        relocation.Id,
        relocation.RootFolderId,
        currentPath,
        relocation.TargetPath,
        relocation.Status,
        relocation.TotalJobs,
        relocation.CompletedJobs,
        relocation.Error,
        relocation.TargetIdentityEnrollmentState);

    private async Task BroadcastAsync(
        RootFolderPathChangeResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            await hubBroadcaster.BroadcastAsync(
                "RootFolderRelocationUpdate",
                result,
                cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            // The relocation state is already committed. Request or transport
            // cancellation may suppress this best-effort publication, but it must
            // not make the durable operation appear to have failed.
            System.Diagnostics.Trace.TraceWarning(
                "Canceled broadcasting root relocation {0}: {1}",
                result.RelocationId,
                exception.Message);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            System.Diagnostics.Trace.TraceWarning(
                "Failed to broadcast root relocation {0}: {1}",
                result.RelocationId,
                exception.Message);
        }
    }

}
