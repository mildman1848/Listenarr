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
using Listenarr.Domain.Common;

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
            || IsLinkedOrUnverifiableEntry(destinationParent))
        {
            throw new IOException(
                "Directory copy requires an existing, non-linked destination parent.");
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
            await PopulateDirectoryCopyStagingAsync(snapshot, stagingRoot);
            if (!stagingCreation.VisiblePathMatches()
                || !await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, stagingRoot)
                || !await SourceSnapshotStillMatchesAsync(snapshot))
            {
                throw new IOException(
                    "Directory copy staging or source changed before publication.");
            }

            try
            {
                Directory.Move(stagingRoot, destinationRoot);
                published = true;
            }
            catch (IOException) when (Directory.Exists(destinationRoot))
            {
                if (await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, destinationRoot))
                {
                    return;
                }

                throw new IOException(
                    "Directory copy destination appeared with conflicting or unexpected content before publication.");
            }

            if (!await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, destinationRoot)
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
        string stagingRoot)
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

            var stagingDirectory = ResolveSnapshotPath(
                stagingRoot,
                relativeDirectory,
                "staging directory");
            if (File.Exists(stagingDirectory)
                || Directory.Exists(stagingDirectory))
            {
                throw new IOException(
                    $"Directory copy staging was unexpectedly occupied: {relativeDirectory}");
            }

            Directory.CreateDirectory(stagingDirectory);
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

            var stagingFile = ResolveSnapshotPath(
                stagingRoot,
                relativeFile,
                "staging file");
            if (File.Exists(stagingFile)
                || Directory.Exists(stagingFile))
            {
                throw new IOException(
                    $"Directory copy staging was unexpectedly occupied: {relativeFile}");
            }

            File.Copy(sourceFile, stagingFile, overwrite: false);
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
                "Copied into an isolated staging snapshot");
        }
    }

    private async Task<bool> SourceSnapshotStillMatchesAsync(
        DirectoryCopySnapshot snapshot)
    {
        if (!TryCaptureDirectoryCopySnapshot(
                snapshot.SourceRoot,
                out var currentSnapshot,
                out _)
            || currentSnapshot == null)
        {
            return false;
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        if (!snapshot.RelativeDirectories.SequenceEqual(
                currentSnapshot.RelativeDirectories,
                comparer)
            || !snapshot.RelativeFiles.SequenceEqual(
                currentSnapshot.RelativeFiles,
                comparer))
        {
            return false;
        }

        foreach (var relativeFile in snapshot.RelativeFiles)
        {
            var sourceFile = ResolveSnapshotPath(
                snapshot.SourceRoot,
                relativeFile,
                "source file");
            if (!File.Exists(sourceFile)
                || IsLinkedOrUnverifiableEntry(sourceFile))
            {
                return false;
            }
        }

        return true;
    }

    private async Task<bool> DirectoryCopySnapshotExactlyMatchesAsync(
        DirectoryCopySnapshot snapshot,
        string candidateRoot)
    {
        if (!Directory.Exists(candidateRoot)
            || IsLinkedOrUnverifiableEntry(candidateRoot)
            || !FileSystemSafety.TryEnumerateTreeWithoutLinks(
                candidateRoot,
                out var candidateFiles,
                out var candidateDirectories,
                out _))
        {
            return false;
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var relativeDirectories = candidateDirectories
            .Select(path => GetVerifiedRelativePath(candidateRoot, path))
            .OrderBy(PathDepth)
            .ThenBy(path => path, comparer)
            .ToArray();
        var relativeFiles = candidateFiles
            .Select(path => GetVerifiedRelativePath(candidateRoot, path))
            .OrderBy(path => path, comparer)
            .ToArray();
        if (!snapshot.RelativeDirectories.SequenceEqual(relativeDirectories, comparer)
            || !snapshot.RelativeFiles.SequenceEqual(relativeFiles, comparer))
        {
            return false;
        }

        foreach (var relativeFile in snapshot.RelativeFiles)
        {
            var sourceFile = ResolveSnapshotPath(
                snapshot.SourceRoot,
                relativeFile,
                "source file");
            var candidateFile = ResolveSnapshotPath(
                candidateRoot,
                relativeFile,
                "candidate file");
            if (!File.Exists(sourceFile)
                || !File.Exists(candidateFile)
                || IsLinkedOrUnverifiableEntry(sourceFile)
                || IsLinkedOrUnverifiableEntry(candidateFile)
                || !await FileSystemSafety.FilesHaveSameContentAsync(
                    sourceFile,
                    candidateFile))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task TryCleanupDirectoryCopyStagingAsync(
        DirectoryCopySnapshot snapshot,
        string stagingRoot)
    {
        try
        {
            if (!Directory.Exists(stagingRoot)
                || IsLinkedOrUnverifiableEntry(stagingRoot)
                || !FileSystemSafety.TryEnumerateTreeWithoutLinks(
                    stagingRoot,
                    out var files,
                    out var directories,
                    out _))
            {
                return;
            }

            var expectedFiles = snapshot.RelativeFiles.ToHashSet(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
            var expectedDirectories = snapshot.RelativeDirectories.ToHashSet(
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);
            var relativeFiles = files
                .Select(path => GetVerifiedRelativePath(stagingRoot, path))
                .ToArray();
            var relativeDirectories = directories
                .Select(path => GetVerifiedRelativePath(stagingRoot, path))
                .ToArray();
            if (relativeFiles.Any(path => !expectedFiles.Contains(path))
                || relativeDirectories.Any(path => !expectedDirectories.Contains(path)))
            {
                return;
            }

            foreach (var relativeFile in relativeFiles)
            {
                var stagingFile = ResolveSnapshotPath(
                    stagingRoot,
                    relativeFile,
                    "staging cleanup file");
                var sourceFile = ResolveSnapshotPath(
                    snapshot.SourceRoot,
                    relativeFile,
                    "source cleanup file");
                if (IsLinkedOrUnverifiableEntry(stagingFile)
                    || !File.Exists(sourceFile)
                    || !await FileSystemSafety.FilesHaveSameContentAsync(
                        sourceFile,
                        stagingFile))
                {
                    return;
                }
            }

            foreach (var relativeFile in relativeFiles)
            {
                File.Delete(ResolveSnapshotPath(
                    stagingRoot,
                    relativeFile,
                    "staging cleanup file"));
            }
            foreach (var relativeDirectory in relativeDirectories
                         .OrderByDescending(PathDepth))
            {
                Directory.Delete(ResolveSnapshotPath(
                    stagingRoot,
                    relativeDirectory,
                    "staging cleanup directory"),
                    recursive: false);
            }
            Directory.Delete(stagingRoot, recursive: false);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            // Uncertain or externally changed staging is preserved for diagnosis.
        }
    }

    private async Task EnsureDirectoryCopyTargetSafeAsync(
        string sourceRoot,
        string destinationRoot,
        string targetPath)
    {
        if (IsLinkedOrUnverifiableEntry(destinationRoot))
        {
            throw new IOException("Directory copy destination root is a symbolic link or reparse point.");
        }

        var equivalent = await TryDetermineFilesystemPathEquivalenceAsync(
            sourceRoot,
            destinationRoot);
        var overlap = await TryDetermineDirectoryOverlapAsync(
            sourceRoot,
            destinationRoot);
        if (equivalent != false || overlap != false)
        {
            throw new IOException(
                "Directory copy destination could not be proven distinct and non-overlapping.");
        }

        if (!FileSystemSafety.TryValidateMutationTarget(
                targetPath,
                [destinationRoot],
                out var normalizedTarget,
                out var validationReason)
            || !PathsMatchForCurrentHost(normalizedTarget, targetPath))
        {
            throw new IOException(
                $"Directory copy destination failed mutation-boundary validation: {validationReason}");
        }
    }

    private async Task<bool> DirectoryCopySnapshotStillMatchesAsync(
        DirectoryCopySnapshot snapshot,
        string destinationRoot) =>
        await SourceSnapshotStillMatchesAsync(snapshot)
        && await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, destinationRoot);

    private static string GetVerifiedRelativePath(string root, string path)
    {
        var relativePath = Path.GetRelativePath(root, path);
        var resolvedPath = ResolveSnapshotPath(root, relativePath, "snapshot entry");
        if (!PathsMatchForCurrentHost(resolvedPath, path))
        {
            throw new IOException("Directory copy snapshot entry escaped its source root.");
        }

        return relativePath;
    }

    private static string ResolveSnapshotPath(
        string root,
        string relativePath,
        string description)
    {
        if (!FileUtils.TryResolveRelativePathWithinBase(
                root,
                relativePath,
                out var resolvedPath))
        {
            throw new IOException(
                $"Directory copy {description} escaped its root: {relativePath}");
        }

        return Path.GetFullPath(resolvedPath);
    }

    private static int PathDepth(string path) =>
        path.Count(character =>
            character == Path.DirectorySeparatorChar
            || character == Path.AltDirectorySeparatorChar);

    private static bool PathsMatchForCurrentHost(string first, string second) =>
        string.Equals(
            Path.GetFullPath(first),
            Path.GetFullPath(second),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
