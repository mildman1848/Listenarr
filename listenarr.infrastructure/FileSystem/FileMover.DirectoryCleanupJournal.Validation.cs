using System.Security.Cryptography;
using System.Text.Json;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static async Task WriteCleanupJournalAsync(
        PinnedDirectoryCreation.PinnedFileEntry journal,
        CleanupJournalPayload payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var envelope = new CleanupJournalEnvelope(
            Convert.ToBase64String(payloadBytes),
            Convert.ToHexString(SHA256.HashData(payloadBytes)));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope);
        await using var stream = journal.OpenWriteStream(4096, asynchronous: false);
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
        stream.Flush(flushToDisk: true);
    }

    private static async Task<bool> ValidateCleanupManifestAsync(
        CleanupJournalPayload payload,
        string candidateRoot,
        bool validateDestination)
    {
        if (!TryGetDirectoryIdentity(candidateRoot, out var rootIdentity)
            || rootIdentity != payload.SourceRootIdentity
            || (validateDestination
                && (!TryGetDirectoryIdentity(
                        payload.DestinationRoot,
                        out var destinationIdentity)
                    || destinationIdentity != payload.DestinationRootIdentity)))
        {
            return false;
        }
        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                candidateRoot,
                out var actualFiles,
                out var actualDirectories,
                out _))
        {
            return false;
        }
        var expectedFilePaths = payload.Files
            .Select(file => file.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        var actualFilePaths = actualFiles
            .Select(path => GetVerifiedRelativePath(candidateRoot, path))
            .ToHashSet(StringComparer.Ordinal);
        var expectedDirectoryPaths = payload.Directories.Keys
            .ToHashSet(StringComparer.Ordinal);
        var actualDirectoryPaths = actualDirectories
            .Select(path => GetVerifiedRelativePath(candidateRoot, path))
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedFilePaths.SetEquals(actualFilePaths)
            || !expectedDirectoryPaths.SetEquals(actualDirectoryPaths))
        {
            return false;
        }

        foreach (var file in payload.Files)
        {
            var candidate = ResolveSnapshotPath(
                candidateRoot,
                file.RelativePath,
                "quarantined cleanup file");
            if (!TryGetRegularFileIdentity(candidate, out var identity)
                || identity != file.Identity)
            {
                return false;
            }
            await using var stream = new FileStream(
                candidate,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length != file.Length
                || !string.Equals(
                    Convert.ToHexString(await SHA256.HashDataAsync(stream)),
                    file.Sha256,
                    StringComparison.Ordinal))
            {
                return false;
            }
            if (validateDestination)
            {
                var destination = ResolveSnapshotPath(
                    payload.DestinationRoot,
                    file.RelativePath,
                    "cleanup destination file");
                if (!File.Exists(destination)
                    || !await FileSystemSafety.FilesHaveSameContentAsync(
                        candidate,
                        destination))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static async Task<bool> DeletePinnedCleanupTreeAsync(
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory,
        CleanupJournalPayload payload,
        string relativeDirectory)
    {
        var expectedFiles = payload.Files
            .Select(file => file.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        var names = Directory.EnumerateFileSystemEntries(directory.FullPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Cast<string>()
            .ToArray();
        if (!directory.VisiblePathMatches())
        {
            return false;
        }

        foreach (var name in names)
        {
            var relative = string.IsNullOrEmpty(relativeDirectory)
                ? name
                : Path.Join(relativeDirectory, name);
            var path = Path.Join(directory.FullPath, name);
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (!payload.Directories.ContainsKey(relative))
                {
                    return false;
                }
                using var childPublication =
                    directory.OpenExistingChildForPublication(name);
                using var child = childPublication.OpenCreatedDirectoryAnchor();
                using var childHandle = child.DuplicateHandleForOperation();
                if (!payload.Directories.TryGetValue(
                        relative,
                        out var expectedDirectoryIdentity)
                    || !TryGetRegularFileIdentity(
                        childHandle,
                        out var childIdentity)
                    || childIdentity != expectedDirectoryIdentity
                    || !await DeletePinnedCleanupTreeAsync(
                        child,
                        payload,
                        relative)
                    || !child.VisiblePathMatches()
                    || Directory.EnumerateFileSystemEntries(path).Any())
                {
                    return false;
                }
                childPublication.DeletePinnedEmptyDirectory(
                    name,
                    immediateWindows: true);
                continue;
            }
            if (!expectedFiles.Contains(relative))
            {
                return false;
            }
            using var file = directory.OpenExistingFile(
                name,
                requireDeleteAccess: true);
            var expectedFile = payload.Files.Single(
                candidate => string.Equals(
                    candidate.RelativePath,
                    relative,
                    StringComparison.Ordinal));
            using var fileHandle = file.DuplicateHandleForOperation();
            if (!TryGetRegularFileIdentity(fileHandle, out var fileIdentity)
                || fileIdentity != expectedFile.Identity
                || !await file.MatchesAsync(
                    expectedFile.Length,
                    expectedFile.Sha256,
                    CancellationToken.None)
                || !directory.VisiblePathMatches()
                || !file.VisiblePathMatches())
            {
                return false;
            }
            file.Delete(immediateWindows: true);
        }

        return directory.VisiblePathMatches()
            && !Directory.EnumerateFileSystemEntries(directory.FullPath).Any();
    }
}
