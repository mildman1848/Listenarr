/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Audiobooks.Enumerations;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private sealed record DirectoryCopySnapshot(
        string SourceRoot,
        IReadOnlyList<string> RelativeDirectories,
        IReadOnlyList<string> RelativeFiles);

    private static bool TryCaptureDirectoryCopySnapshot(
        string sourceDirectory,
        out DirectoryCopySnapshot? snapshot,
        out string reason)
    {
        snapshot = null;
        reason = string.Empty;
        try
        {
            var sourceRoot = Path.GetFullPath(sourceDirectory);
            if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                    sourceRoot,
                    out var files,
                    out var directories,
                    out reason))
            {
                return false;
            }

            var relativeDirectories = directories
                .Select(path => GetVerifiedRelativePath(sourceRoot, path))
                .OrderBy(PathDepth)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToArray();
            var relativeFiles = files
                .Select(path => GetVerifiedRelativePath(sourceRoot, path))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray();
            snapshot = new DirectoryCopySnapshot(
                sourceRoot,
                relativeDirectories,
                relativeFiles);
            return true;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            reason = $"The source directory snapshot could not be captured safely: {exception.GetType().Name}.";
            return false;
        }
    }

    private async Task CopyDirectorySnapshotAsync(
        DirectoryCopySnapshot snapshot,
        string destinationDirectory)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        await EnsureDirectoryCopyTargetSafeAsync(
            snapshot.SourceRoot,
            destinationRoot,
            destinationRoot);

        if (Directory.Exists(destinationRoot))
        {
            if (await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, destinationRoot))
            {
                return;
            }

            throw new IOException(
                "Directory copy destination already exists with conflicting or unexpected content.");
        }

        var destinationParent = Path.GetDirectoryName(destinationRoot);
        if (string.IsNullOrWhiteSpace(destinationParent)
            || !Directory.Exists(destinationParent)
            || IsLinkedOrUnverifiableEntry(destinationParent)
            || !TryResolvePhysicalPath(destinationParent, out var parentResolution)
            || parentResolution.EntryKind != PhysicalPathEntryKind.Directory
            || parentResolution.EncounteredLink)
        {
            throw new IOException(
                "Directory copy requires an existing destination parent with no linked path components.");
        }

        var stagingName = $".{Path.GetFileName(destinationRoot)}.listenarr-copy-{Guid.NewGuid():N}";
        var stagingRoot = Path.Join(destinationParent, stagingName);
        using var stagingCreation = ExclusiveDirectoryCreator.TryCreatePinned(
            destinationParent,
            stagingName);
        if (!stagingCreation.Created || !stagingCreation.VisiblePathMatches())
        {
            throw new IOException(
                "Directory copy staging could not be created with exclusive ownership.");
        }

        var published = false;
        try
        {
            using var stagingAnchor = stagingCreation.OpenCreatedDirectoryAnchor();
            await PopulateDirectoryCopyStagingAsync(snapshot, stagingAnchor);
            if (!stagingCreation.VisiblePathMatches()
                || !stagingAnchor.VisiblePathMatches()
                || !await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, stagingRoot)
                || !await SourceSnapshotStillMatchesAsync(snapshot))
            {
                throw new IOException(
                    "Directory copy staging or source changed before publication.");
            }

            if (!stagingAnchor.VisiblePathMatches())
            {
                throw new IOException(
                    "Directory copy staging identity changed before publication.");
            }
            if (BeforeDirectoryCopyPublicationForTestAsync != null)
            {
                await BeforeDirectoryCopyPublicationForTestAsync(stagingRoot);
            }
            if (!stagingAnchor.VisiblePathMatches())
            {
                throw new IOException(
                    "Directory copy staging identity changed at the publication boundary.");
            }

            try
            {
                Directory.Move(stagingRoot, destinationRoot);
                published = true;
                if (!stagingAnchor.VisiblePathMatches(destinationRoot))
                {
                    throw new IOException(
                        "The published destination does not identify the pinned staging directory.");
                }
            }
            catch (IOException) when (!published && Directory.Exists(destinationRoot))
            {
                if (await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, destinationRoot))
                {
                    return;
                }

                throw new IOException(
                    "Directory copy destination appeared with conflicting or unexpected content before publication.");
            }

            if (!stagingAnchor.VisiblePathMatches(destinationRoot)
                || !await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, destinationRoot)
                || !await SourceSnapshotStillMatchesAsync(snapshot))
            {
                throw new IOException(
                    "The published directory snapshot could not be verified exactly.");
            }
        }
        finally
        {
            if (!published && stagingCreation.VisiblePathMatches())
            {
                await TryCleanupDirectoryCopyStagingAsync(snapshot, stagingRoot);
            }
        }
    }

    private async Task PopulateDirectoryCopyStagingAsync(
        DirectoryCopySnapshot snapshot,
        PinnedDirectoryCreation.PinnedDirectoryAnchor stagingAnchor)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var anchors = new Dictionary<string, PinnedDirectoryCreation.PinnedDirectoryAnchor>(comparer)
        {
            [string.Empty] = stagingAnchor
        };
        var ownedAnchors = new List<PinnedDirectoryCreation.PinnedDirectoryAnchor>();
        try
        {
            foreach (var relativeDirectory in snapshot.RelativeDirectories)
            {
                var sourceDirectory = ResolveSnapshotPath(
                    snapshot.SourceRoot,
                    relativeDirectory,
                    "source directory");
                if (!Directory.Exists(sourceDirectory)
                    || IsLinkedOrUnverifiableEntry(sourceDirectory))
                {
                    throw new IOException(
                        $"Directory copy source changed after verification: {relativeDirectory}");
                }

                var parentKey = NormalizeRelativeDirectoryKey(
                    Path.GetDirectoryName(relativeDirectory));
                if (!anchors.TryGetValue(parentKey, out var parentAnchor))
                {
                    throw new IOException(
                        $"Directory copy staging parent was not pinned: {relativeDirectory}");
                }

                using var creation = parentAnchor.TryCreateChild(
                    Path.GetFileName(relativeDirectory));
                if (!creation.Created || !creation.VisiblePathMatches())
                {
                    throw new IOException(
                        $"Directory copy staging was unexpectedly occupied: {relativeDirectory}");
                }

                var childAnchor = creation.OpenCreatedDirectoryAnchor();
                if (!childAnchor.VisiblePathMatches())
                {
                    childAnchor.Dispose();
                    throw new IOException(
                        $"Directory copy staging identity changed: {relativeDirectory}");
                }

                anchors.Add(relativeDirectory, childAnchor);
                ownedAnchors.Add(childAnchor);
            }

            if (AfterDirectoryCopyStagingDirectoriesCreatedForTestAsync != null)
            {
                await AfterDirectoryCopyStagingDirectoriesCreatedForTestAsync(stagingAnchor.FullPath);
            }

            foreach (var relativeFile in snapshot.RelativeFiles)
            {
                var sourceFile = ResolveSnapshotPath(
                    snapshot.SourceRoot,
                    relativeFile,
                    "source file");
                if (!File.Exists(sourceFile)
                    || Directory.Exists(sourceFile)
                    || IsLinkedOrUnverifiableEntry(sourceFile))
                {
                    throw new IOException(
                        $"Directory copy source changed after verification: {relativeFile}");
                }

                var parentKey = NormalizeRelativeDirectoryKey(
                    Path.GetDirectoryName(relativeFile));
                if (!anchors.TryGetValue(parentKey, out var parentAnchor))
                {
                    throw new IOException(
                        $"Directory copy staging file parent was not pinned: {relativeFile}");
                }

                var childName = Path.GetFileName(relativeFile);
                await parentAnchor.CopyNewFileFromAsync(
                    sourceFile,
                    childName,
                    CancellationToken.None);
                var stagingFile = ResolveSnapshotPath(
                    stagingAnchor.FullPath,
                    relativeFile,
                    "staging file");
                if (IsLinkedOrUnverifiableEntry(stagingFile)
                    || !await FileSystemSafety.FilesHaveSameContentAsync(sourceFile, stagingFile))
                {
                    throw new IOException(
                        $"Directory copy staging content could not be verified: {relativeFile}");
                }
                LogMutation(
                    FileMutationOutcome.Success,
                    FileAction.Copy,
                    sourceFile,
                    stagingFile,
                    "Copied into an isolated pinned staging snapshot");
            }
        }
        finally
        {
            for (var index = ownedAnchors.Count - 1; index >= 0; index--)
            {
                ownedAnchors[index].Dispose();
            }
        }
    }

    private static string NormalizeRelativeDirectoryKey(string? path) =>
        string.IsNullOrEmpty(path) || path == "."
            ? string.Empty
            : path;

}
