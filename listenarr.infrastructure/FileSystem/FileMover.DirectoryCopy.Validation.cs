/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
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
            || snapshot.SourceRootIdentity != currentSnapshot.SourceRootIdentity
            || snapshot.Files.Count != currentSnapshot.Files.Count)
        {
            return false;
        }

        foreach (var relativeDirectory in snapshot.RelativeDirectories)
        {
            if (!snapshot.DirectoryIdentities.TryGetValue(
                    relativeDirectory,
                    out var expectedIdentity)
                || !currentSnapshot.DirectoryIdentities.TryGetValue(
                    relativeDirectory,
                    out var currentIdentity)
                || expectedIdentity != currentIdentity)
            {
                return false;
            }
        }

        for (var index = 0; index < snapshot.Files.Count; index++)
        {
            var fileSnapshot = snapshot.Files[index];
            var currentFileSnapshot = currentSnapshot.Files[index];
            if (!comparer.Equals(
                    fileSnapshot.RelativePath,
                    currentFileSnapshot.RelativePath)
                || fileSnapshot.Identity != currentFileSnapshot.Identity)
            {
                return false;
            }

            var sourceFile = ResolveSnapshotPath(
                snapshot.SourceRoot,
                fileSnapshot.RelativePath,
                "source file");
            if (!File.Exists(sourceFile)
                || IsLinkedOrUnverifiableEntry(sourceFile)
                || !TryGetRegularFileIdentity(sourceFile, out var currentIdentity)
                || currentIdentity != fileSnapshot.Identity)
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
            || !snapshot.Files
                .Select(file => file.RelativePath)
                .SequenceEqual(relativeFiles, comparer))
        {
            return false;
        }

        foreach (var fileSnapshot in snapshot.Files)
        {
            var sourceFile = ResolveSnapshotPath(
                snapshot.SourceRoot,
                fileSnapshot.RelativePath,
                "source file");
            var candidateFile = ResolveSnapshotPath(
                candidateRoot,
                fileSnapshot.RelativePath,
                "candidate file");
            if (!File.Exists(sourceFile)
                || !File.Exists(candidateFile)
                || IsLinkedOrUnverifiableEntry(sourceFile)
                || IsLinkedOrUnverifiableEntry(candidateFile)
                || !TryGetRegularFileIdentity(sourceFile, out var currentIdentity)
                || currentIdentity != fileSnapshot.Identity
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

            var expectedFiles = snapshot.Files
                .Select(file => file.RelativePath)
                .ToHashSet(
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
