/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Security.Cryptography;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private const int FileMoveLockStripeCount = 4096;
    private static readonly object FileMoveGateRegistryLock = new();
    private static readonly Dictionary<string, FileMoveGateEntry> FileMoveGates = [];

    private sealed class FileMoveGateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int UserCount { get; set; }
    }

    private sealed class FileMoveGateLease(
        string key,
        FileMoveGateEntry entry,
        IReadOnlyList<FileStream> stripeLocks,
        string sourceIdentity,
        string destinationIdentity) : IDisposable
    {
        private FileMoveGateEntry? _entry = entry;

        public string SourceIdentity { get; } = sourceIdentity;
        public string DestinationIdentity { get; } = destinationIdentity;
        private IReadOnlyList<FileStream>? _stripeLocks = stripeLocks;

        public void Dispose()
        {
            var releasedEntry = Interlocked.Exchange(ref _entry, null);
            if (releasedEntry == null)
            {
                return;
            }

            var locks = Interlocked.Exchange(ref _stripeLocks, null);
            if (locks != null)
            {
                foreach (var stripeLock in locks.Reverse())
                {
                    stripeLock.Dispose();
                }
            }

            releasedEntry.Semaphore.Release();
            lock (FileMoveGateRegistryLock)
            {
                releasedEntry.UserCount--;
                if (releasedEntry.UserCount == 0)
                {
                    FileMoveGates.Remove(key);
                    releasedEntry.Semaphore.Dispose();
                }
            }
        }
    }

    private async Task<FileMoveGateLease?> TryAcquireFileMoveGateAsync(
        string sourceFile,
        string destinationFile)
    {
        if (await IsLinkedFilesystemAliasAsync(sourceFile, destinationFile))
        {
            LogBlockedAlias(sourceFile, destinationFile);
            return null;
        }

        var sourceIdentity = await ResolveFileMoveLockIdentityAsync(sourceFile);
        var destinationIdentity = await ResolveFileMoveLockIdentityAsync(
            destinationFile);
        if (sourceIdentity == null || destinationIdentity == null)
        {
            _logger.LogWarning(
                "Blocked file move because endpoint identity could not be resolved: {Source} -> {Destination}",
                LogRedaction.SanitizeFilePath(sourceFile),
                LogRedaction.SanitizeFilePath(destinationFile));
            return null;
        }

        if (await IsLinkedFilesystemAliasAsync(sourceFile, destinationFile))
        {
            LogBlockedAlias(sourceFile, destinationFile);
            return null;
        }

        var key = GetFileMoveGateKey(sourceIdentity, destinationIdentity);
        FileMoveGateEntry entry;
        lock (FileMoveGateRegistryLock)
        {
            if (!FileMoveGates.TryGetValue(key, out entry!))
            {
                entry = new FileMoveGateEntry();
                FileMoveGates.Add(key, entry);
            }

            entry.UserCount++;
        }

        await entry.Semaphore.WaitAsync();
        var stripeLocks = new List<FileStream>();
        try
        {
            foreach (var lockPath in GetFileMoveStripeLockPaths(
                sourceIdentity,
                destinationIdentity))
            {
                FileStream? stream = null;
                for (var attempt = 0; attempt < 300 && stream == null; attempt++)
                {
                    try
                    {
                        stream = new FileStream(
                            lockPath,
                            FileMode.OpenOrCreate,
                            FileAccess.ReadWrite,
                            FileShare.None,
                            bufferSize: 1,
                            FileOptions.None);
                    }
                    catch (IOException) when (attempt < 299)
                    {
                        await Task.Delay(100);
                    }
                }

                if (stream == null)
                {
                    throw new IOException("Timed out acquiring a file-move stripe lock.");
                }

                stripeLocks.Add(stream);
            }

            var lease = new FileMoveGateLease(
                key,
                entry,
                stripeLocks,
                sourceIdentity,
                destinationIdentity);
            if (await IsLinkedFilesystemAliasAsync(sourceFile, destinationFile))
            {
                lease.Dispose();
                LogBlockedAlias(sourceFile, destinationFile);
                return null;
            }

            return lease;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            new FileMoveGateLease(
                key,
                entry,
                stripeLocks,
                sourceIdentity,
                destinationIdentity).Dispose();
            _logger.LogWarning(
                exception,
                "Blocked file move because cross-process path locks were unavailable: {Source} -> {Destination}",
                LogRedaction.SanitizeFilePath(sourceFile),
                LogRedaction.SanitizeFilePath(destinationFile));
            return null;
        }
    }

    private void LogBlockedAlias(string sourceFile, string destinationFile) =>
        _logger.LogWarning(
            "Blocked file move because source and destination are linked aliases: {Source} -> {Destination}",
            LogRedaction.SanitizeFilePath(sourceFile),
            LogRedaction.SanitizeFilePath(destinationFile));

    private async ValueTask<string?> ResolveFileMoveLockIdentityAsync(
        string path)
    {
        var resolver = _semanticsResolver ?? new FileSystemSemanticsResolver();
        var resolution = await resolver.ResolveAsync(path);
        if (resolution.State != PathIdentityState.Valid
            || resolution.Semantics.CaseSensitivity
                == FileSystemCaseSensitivity.Unknown)
        {
            return null;
        }

        var canonicalPath = FileSystemPathIdentity.Canonicalize(
            resolution.CanonicalPath ?? Path.GetFullPath(path),
            resolution.Semantics.Syntax);
        if (!TryResolveLinkedPathComponents(canonicalPath, out var identity))
        {
            return null;
        }

        return resolution.Semantics.CaseSensitivity
                == FileSystemCaseSensitivity.Insensitive
            ? identity.ToUpperInvariant()
            : identity;
    }

    private static bool TryResolveLinkedPathComponents(
        string path,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            var lexicalPath = root;
            var physicalPath = root;
            var relative = fullPath[root.Length..];
            foreach (var segment in relative.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                lexicalPath = Path.Join(lexicalPath, segment);
                var exists = File.Exists(lexicalPath)
                    || Directory.Exists(lexicalPath);
                if (!exists)
                {
                    physicalPath = Path.Join(physicalPath, segment);
                    continue;
                }

                var attributes = File.GetAttributes(lexicalPath);
                var info = (attributes & FileAttributes.Directory) != 0
                    ? (FileSystemInfo)new DirectoryInfo(lexicalPath)
                    : new FileInfo(lexicalPath);
                var target = (attributes & FileAttributes.ReparsePoint) != 0
                    ? info.ResolveLinkTarget(returnFinalTarget: true)
                    : null;
                physicalPath = Path.GetFullPath(
                    target?.FullName ?? Path.Join(physicalPath, segment));
            }

            resolvedPath = physicalPath;
            return true;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            return false;
        }
    }

    private static IReadOnlyList<string> GetFileMoveStripeLockPaths(
        string sourceIdentity,
        string destinationIdentity)
    {
        var directory = GetFileMoveLockDirectory();
        return new[] { sourceIdentity, destinationIdentity }
            .Select(GetFileMoveLockStripe)
            .Distinct()
            .Order()
            .Select(stripe => Path.Join(directory, $"stripe-{stripe:D4}.lock"))
            .ToArray();
    }

    private static int GetFileMoveLockStripe(string path)
    {
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(path));
        return (int)(BitConverter.ToUInt32(hash, 0) % FileMoveLockStripeCount);
    }

    private static string GetFileMoveLockDirectory()
    {
        var localData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new IOException(
                "A per-user application-data directory is required for file-move locks.");
        }

        var directory = Path.Join(localData, "Listenarr", "file-move-locks");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }

        if (!TryValidateStateDirectory(directory))
        {
            throw new IOException("The file-move lock directory is unsafe.");
        }

        return directory;
    }

    private static string GetFileMoveGateKey(
        string sourceIdentity,
        string destinationIdentity)
    {
        var first = sourceIdentity;
        var second = destinationIdentity;
        if (string.CompareOrdinal(first, second) > 0)
        {
            (first, second) = (second, first);
        }

        return HashPathIdentity($"{first}\0{second}");
    }
}
