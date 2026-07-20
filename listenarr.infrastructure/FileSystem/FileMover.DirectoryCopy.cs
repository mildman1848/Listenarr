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
        Directory.CreateDirectory(destinationRoot);
        await EnsureDirectoryCopyTargetSafeAsync(
            snapshot.SourceRoot,
            destinationRoot,
            destinationRoot);

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

            var destinationPath = ResolveSnapshotPath(
                destinationRoot,
                relativeDirectory,
                "destination directory");
            await EnsureDirectoryCopyTargetSafeAsync(
                snapshot.SourceRoot,
                destinationRoot,
                destinationPath);
            if (File.Exists(destinationPath)
                || (Directory.Exists(destinationPath)
                    && IsLinkedOrUnverifiableEntry(destinationPath)))
            {
                throw new IOException(
                    $"Directory copy destination is unsafe: {relativeDirectory}");
            }

            Directory.CreateDirectory(destinationPath);
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

            var destinationFile = ResolveSnapshotPath(
                destinationRoot,
                relativeFile,
                "destination file");
            await EnsureDirectoryCopyTargetSafeAsync(
                snapshot.SourceRoot,
                destinationRoot,
                destinationFile);
            if (Directory.Exists(destinationFile)
                || IsLinkedOrUnverifiableEntry(destinationFile))
            {
                throw new IOException(
                    $"Directory copy destination is unsafe: {relativeFile}");
            }

            if (File.Exists(destinationFile)
                && await FileSystemSafety.FilesHaveSameContentAsync(sourceFile, destinationFile))
            {
                LogMutation(
                    FileMutationOutcome.Skipped,
                    FileAction.Copy,
                    sourceFile,
                    destinationFile,
                    "Destination already has identical content");
                continue;
            }

            File.Copy(sourceFile, destinationFile, overwrite: true);
            LogMutation(
                FileMutationOutcome.Success,
                FileAction.Copy,
                sourceFile,
                destinationFile);
        }

        if (!await DirectoryCopySnapshotStillMatchesAsync(snapshot, destinationRoot))
        {
            throw new IOException(
                "Directory copy source changed after verification; the verified destination snapshot was preserved.");
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
        string destinationRoot)
    {
        await EnsureDirectoryCopyTargetSafeAsync(
            snapshot.SourceRoot,
            destinationRoot,
            destinationRoot);
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

        foreach (var relativeDirectory in snapshot.RelativeDirectories)
        {
            var destinationDirectory = ResolveSnapshotPath(
                destinationRoot,
                relativeDirectory,
                "destination directory");
            await EnsureDirectoryCopyTargetSafeAsync(
                snapshot.SourceRoot,
                destinationRoot,
                destinationDirectory);
            if (!Directory.Exists(destinationDirectory)
                || IsLinkedOrUnverifiableEntry(destinationDirectory))
            {
                return false;
            }
        }

        foreach (var relativeFile in snapshot.RelativeFiles)
        {
            var sourceFile = ResolveSnapshotPath(
                snapshot.SourceRoot,
                relativeFile,
                "source file");
            var destinationFile = ResolveSnapshotPath(
                destinationRoot,
                relativeFile,
                "destination file");
            await EnsureDirectoryCopyTargetSafeAsync(
                snapshot.SourceRoot,
                destinationRoot,
                destinationFile);
            if (!File.Exists(sourceFile)
                || !File.Exists(destinationFile)
                || IsLinkedOrUnverifiableEntry(sourceFile)
                || IsLinkedOrUnverifiableEntry(destinationFile)
                || !await FileSystemSafety.FilesHaveSameContentAsync(
                    sourceFile,
                    destinationFile))
            {
                return false;
            }
        }

        return true;
    }

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
