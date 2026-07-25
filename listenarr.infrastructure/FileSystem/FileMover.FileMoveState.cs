/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static bool FileMoveStateExists(FileMoveStatePaths state) =>
        PathEntryExists(state.SourceStateDirectory)
        || PathEntryExists(state.DestinationStateDirectory);

    private static FileMoveStatePaths GetFileMoveStatePaths(
        string sourceFile,
        string destinationFile,
        string sourceIdentity,
        string destinationIdentity)
    {
        var normalizedSource = Path.GetFullPath(sourceFile);
        var normalizedDestination = Path.GetFullPath(destinationFile);
        var token = HashPathIdentity($"{sourceIdentity}\0{destinationIdentity}");
        var sourceStateDirectory = Path.Join(
            Path.GetDirectoryName(normalizedSource)!,
            $".listenarr-file-source-{token}.state");
        var destinationStateDirectory = Path.Join(
            Path.GetDirectoryName(normalizedDestination)!,
            $".listenarr-file-destination-{token}.state");
        return new FileMoveStatePaths(
            sourceStateDirectory,
            destinationStateDirectory,
            Path.Join(sourceStateDirectory, "source.claim"),
            Path.Join(destinationStateDirectory, "destination.stage"),
            Path.Join(destinationStateDirectory, "destination.previous"),
            Path.Join(sourceStateDirectory, "replacement-generation.fence"));
    }

    private static string HashPathIdentity(string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..24];

    private static void CreatePrivateStateDirectory(string path)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(temporaryPath);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temporaryPath,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }

            if (!TryValidateStateDirectory(temporaryPath)
                || Directory.EnumerateFileSystemEntries(temporaryPath).Any())
            {
                throw new IOException("Temporary file-move state is unsafe.");
            }

            // Directory.Move is the exclusive ownership claim: it fails if another
            // process already owns the deterministic state directory.
            Directory.Move(temporaryPath, path);
        }
        finally
        {
            if (Directory.Exists(temporaryPath)
                && !Directory.EnumerateFileSystemEntries(temporaryPath).Any())
            {
                Directory.Delete(temporaryPath);
            }
        }
    }

    private static bool StateDirectoryContainsOnly(
        string directoryPath,
        params string[] allowedPaths)
    {
        if (!Directory.Exists(directoryPath))
        {
            return true;
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var allowed = allowedPaths
            .Select(Path.GetFullPath)
            .ToHashSet(comparer);
        return Directory.EnumerateFileSystemEntries(directoryPath)
            .Select(Path.GetFullPath)
            .All(allowed.Contains);
    }

    private static bool TryValidateStateDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        var mode = File.GetUnixFileMode(path);
        var disallowed = UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;
        return (mode & disallowed) == 0;
    }

    private void RestoreUncommittedFileMove(
        string sourceFile,
        string destinationFile,
        FileMoveStatePaths state)
    {
        TryRestoreStateFile(state.SourceClaim, sourceFile);
        TryDeleteFile(state.DestinationStage);
        TryRestoreStateFile(state.DestinationPrevious, destinationFile);
        TryDeleteEmptyStateDirectories(state);
    }

    // This protocol provides process-crash recovery. It does not claim
    // power-loss durability for directory entries on every supported filesystem.
    private static void WriteGenerationFence(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void TryRestoreStateFile(string statePath, string originalPath)
    {
        if (!File.Exists(statePath))
        {
            return;
        }

        try
        {
            if (!PathEntryExists(originalPath))
            {
                File.Move(statePath, originalPath, overwrite: false);
                return;
            }

            _logger.LogWarning(
                "Preserved file-move state {StatePath} because its original path was recreated at {Original}",
                LogRedaction.SanitizeFilePath(statePath),
                LogRedaction.SanitizeFilePath(originalPath));
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Unable to restore file-move state {StatePath} to {Original}; both paths were preserved",
                LogRedaction.SanitizeFilePath(statePath),
                LogRedaction.SanitizeFilePath(originalPath));
        }
    }

    private static bool FileMoveStateHasConflicts(
        string sourceFile,
        string destinationFile,
        FileMoveStatePaths state) =>
        (PathEntryExists(sourceFile) && File.Exists(state.SourceClaim))
        || (PathEntryExists(destinationFile) && File.Exists(state.DestinationStage))
        || (PathEntryExists(destinationFile) && File.Exists(state.DestinationPrevious));

    private static void TryDeleteEmptyStateDirectories(FileMoveStatePaths state)
    {
        TryDeleteEmptyStateDirectory(state.SourceStateDirectory);
        if (!string.Equals(
                state.SourceStateDirectory,
                state.DestinationStateDirectory,
                StringComparison.Ordinal))
        {
            TryDeleteEmptyStateDirectory(state.DestinationStateDirectory);
        }
    }

    private static void TryDeleteEmptyStateDirectory(string path)
    {
        if (Directory.Exists(path)
            && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }

    private static bool PathEntryExists(string path) =>
        File.Exists(path) || Directory.Exists(path);
}
