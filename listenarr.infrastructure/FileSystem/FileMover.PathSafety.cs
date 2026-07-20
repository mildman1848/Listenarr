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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private async Task<bool> IsSameFilesystemPathAsync(string source, string destination) =>
        await TryDetermineFilesystemPathEquivalenceAsync(source, destination) == true;

    private async Task<bool?> TryDetermineDirectoryOverlapAsync(
        string source,
        string destination)
    {
        var sourcePath = Path.GetFullPath(
            FileSystemPathIdentity.ResolveNativeAbsolutePath(source));
        var destinationPath = Path.GetFullPath(
            FileSystemPathIdentity.ResolveNativeAbsolutePath(destination));
        if (_semanticsResolver == null
            || !TryResolvePhysicalPath(sourcePath, out var resolvedSourcePath)
            || !TryResolvePhysicalPath(destinationPath, out var resolvedDestinationPath))
        {
            return null;
        }

        try
        {
            var resolution = await _semanticsResolver.ResolveAsync(sourcePath);
            if (resolution.State != PathIdentityState.Valid)
            {
                return null;
            }

            return FileSystemPathIdentity.IsSameOrInside(
                    resolvedDestinationPath,
                    resolvedSourcePath,
                    resolution.Semantics)
                || FileSystemPathIdentity.IsSameOrInside(
                    resolvedSourcePath,
                    resolvedDestinationPath,
                    resolution.Semantics);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            _logger.LogDebug(
                exception,
                "Filesystem identity resolution failed while checking directory overlap for {Source} and {Destination}",
                LogRedaction.SanitizeFilePath(sourcePath),
                LogRedaction.SanitizeFilePath(destinationPath));
            return null;
        }
    }

    private async Task<bool?> TryDetermineFilesystemPathEquivalenceAsync(
        string source,
        string destination)
    {
        var sourcePath = Path.GetFullPath(
            FileSystemPathIdentity.ResolveNativeAbsolutePath(source));
        var destinationPath = Path.GetFullPath(
            FileSystemPathIdentity.ResolveNativeAbsolutePath(destination));
        if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
        {
            return true;
        }

        if (_semanticsResolver == null
            || !TryResolvePhysicalPath(sourcePath, out var resolvedSourcePath)
            || !TryResolvePhysicalPath(destinationPath, out var resolvedDestinationPath))
        {
            return null;
        }

        if (string.Equals(resolvedSourcePath, resolvedDestinationPath, StringComparison.Ordinal))
        {
            return true;
        }

        try
        {
            var resolution = await _semanticsResolver.ResolveAsync(sourcePath);
            return resolution.State == PathIdentityState.Valid
                ? FileSystemPathIdentity.AreEquivalent(
                    resolvedSourcePath,
                    resolvedDestinationPath,
                    resolution.Semantics)
                : null;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            _logger.LogDebug(
                exception,
                "Filesystem identity resolution failed while comparing {Source} and {Destination}; destructive idempotent cleanup will be disabled",
                LogRedaction.SanitizeFilePath(sourcePath),
                LogRedaction.SanitizeFilePath(destinationPath));
            return null;
        }
    }

    private static bool TryResolvePhysicalPath(string path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return false;
            }

            var currentLexicalPath = root;
            var currentResolvedPath = Path.GetFullPath(root);
            var segments = Path.GetRelativePath(root, fullPath).Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                var candidatePath = Path.Join(currentLexicalPath, segment);
                if (!File.Exists(candidatePath) && !Directory.Exists(candidatePath))
                {
                    for (var remainingIndex = index; remainingIndex < segments.Length; remainingIndex++)
                    {
                        currentResolvedPath = Path.Join(currentResolvedPath, segments[remainingIndex]);
                    }

                    resolvedPath = Path.GetFullPath(currentResolvedPath);
                    return true;
                }

                var attributes = File.GetAttributes(candidatePath);
                var info = (attributes & FileAttributes.Directory) != 0
                    ? (FileSystemInfo)new DirectoryInfo(candidatePath)
                    : new FileInfo(candidatePath);
                var target = (attributes & FileAttributes.ReparsePoint) != 0
                    ? info.ResolveLinkTarget(returnFinalTarget: true)
                    : null;
                currentResolvedPath = Path.GetFullPath(
                    target?.FullName ?? Path.Join(currentResolvedPath, segment));
                currentLexicalPath = candidatePath;
            }

            resolvedPath = Path.GetFullPath(currentResolvedPath);
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsLinkedOrUnverifiableEntry(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }
}
