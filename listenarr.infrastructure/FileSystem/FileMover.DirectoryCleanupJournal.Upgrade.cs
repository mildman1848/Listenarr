using System.Security.Cryptography;
using System.Text.Json;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static string ComputeCleanupManifestHash(
        int version,
        IReadOnlyList<CleanupJournalFile> files,
        IReadOnlyDictionary<string, RegularFileIdentity> directories)
    {
        byte[] manifestBytes;
        if (version == 1)
        {
            manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Files = files.Select(file => new
                {
                    file.RelativePath,
                    file.Identity,
                    file.Length,
                    file.Sha256
                }).ToArray(),
                Directories = directories
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToArray()
            });
        }
        else if (version == 2)
        {
            manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Files = files,
                Directories = directories
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToArray()
            });
        }
        else
        {
            throw new InvalidOperationException(
                "The directory cleanup journal version is unsupported.");
        }

        return Convert.ToHexString(SHA256.HashData(manifestBytes));
    }

    private async Task<CleanupJournalPayload?> UpgradeLegacyCleanupJournalAsync(
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string journalName,
        CleanupJournalPayload payload,
        PinnedDirectoryCreation.PinnedDirectoryAnchor quarantine)
    {
        if (payload.Version != 1
            || payload.Files.Any(file => file.DestinationIdentity.HasValue)
            || !FileSystemSafety.TryEnumerateTreeWithoutLinks(
                quarantine.FullPath,
                out var actualFiles,
                out var actualDirectories,
                out _))
        {
            return null;
        }

        Dictionary<string, CleanupJournalFile> expectedFiles;
        try
        {
            expectedFiles = payload.Files.ToDictionary(
                file => file.RelativePath,
                StringComparer.Ordinal);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var remainingFiles = new List<CleanupJournalFile>(actualFiles.Count);
        foreach (var actualPath in actualFiles)
        {
            var relative = GetVerifiedRelativePath(
                quarantine.FullPath,
                actualPath);
            if (!expectedFiles.TryGetValue(relative, out var expected)
                || !TryGetRegularFileIdentity(actualPath, out var actualIdentity)
                || actualIdentity != expected.Identity)
            {
                return null;
            }

            await using var sourceStream = new FileStream(
                actualPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (sourceStream.Length != expected.Length
                || !string.Equals(
                    Convert.ToHexString(
                        await SHA256.HashDataAsync(sourceStream)),
                    expected.Sha256,
                    StringComparison.Ordinal))
            {
                return null;
            }

            var destinationPath = ResolveSnapshotPath(
                payload.DestinationRoot,
                relative,
                "legacy cleanup destination file");
            if (!TryGetRegularFileIdentity(
                    destinationPath,
                    out var destinationIdentity)
                || !await FileSystemSafety.FilesHaveSameContentAsync(
                    actualPath,
                    destinationPath))
            {
                return null;
            }

            remainingFiles.Add(expected with
            {
                DestinationIdentity = destinationIdentity
            });
        }

        var remainingDirectories =
            new Dictionary<string, RegularFileIdentity>(StringComparer.Ordinal);
        foreach (var actualPath in actualDirectories)
        {
            var relative = GetVerifiedRelativePath(
                quarantine.FullPath,
                actualPath);
            if (!payload.Directories.TryGetValue(
                    relative,
                    out var expectedIdentity)
                || !TryGetDirectoryIdentity(actualPath, out var actualIdentity)
                || actualIdentity != expectedIdentity)
            {
                return null;
            }

            remainingDirectories.Add(relative, expectedIdentity);
        }

        var upgraded = payload with
        {
            Version = 2,
            Files = remainingFiles,
            Directories = remainingDirectories,
            ManifestHash = string.Empty
        };
        upgraded = upgraded with
        {
            ManifestHash = ComputeCleanupManifestHash(
                upgraded.Version,
                upgraded.Files,
                upgraded.Directories)
        };
        if (!await ValidateCleanupManifestAsync(
                upgraded,
                quarantine.FullPath,
                validateDestination: true))
        {
            return null;
        }

        await ReplaceCleanupJournalAsync(parent, journalName, upgraded);
        return upgraded;
    }

    private static async Task ReplaceCleanupJournalAsync(
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string journalName,
        CleanupJournalPayload payload)
    {
        var temporaryName =
            $"{journalName}.upgrade-{Guid.NewGuid():N}.tmp";
        using var temporary = parent.CreateNewFile(
            temporaryName,
            hiddenFile: true);
        var replaced = false;
        try
        {
            await WriteCleanupJournalAsync(temporary, payload);
            using var predecessor = parent.OpenExistingFile(
                journalName,
                requireDeleteAccess: false);
            temporary.ReplaceWithinParent(journalName, predecessor);
            replaced = true;
            parent.FlushDirectoryEntry();
        }
        catch
        {
            if (!replaced && temporary.VisiblePathMatches())
            {
                try
                {
                    temporary.Delete(immediateWindows: true);
                }
                catch (Exception exception) when (exception is
                    IOException or UnauthorizedAccessException
                        or InvalidOperationException
                        or System.ComponentModel.Win32Exception)
                {
                    // Preserve the original journal as the authoritative recovery proof.
                }
            }

            throw;
        }

        using var published = parent.OpenExistingFile(
            journalName,
            requireDeleteAccess: false);
        var reloaded = await ReadCleanupJournalAsync(published);
        if (reloaded == null
            || reloaded.Version != 2
            || reloaded.OperationId != payload.OperationId
            || !string.Equals(
                reloaded.ManifestHash,
                payload.ManifestHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The upgraded directory cleanup journal could not be revalidated.");
        }
    }
}
