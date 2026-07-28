using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private sealed record CleanupJournalFile(
        string RelativePath,
        RegularFileIdentity Identity,
        long Length,
        string Sha256);

    private sealed record CleanupJournalPayload(
        int Version,
        Guid OperationId,
        string SourceRoot,
        string DestinationRoot,
        RegularFileIdentity SourceRootIdentity,
        RegularFileIdentity DestinationRootIdentity,
        string QuarantineName,
        string ManifestHash,
        IReadOnlyList<CleanupJournalFile> Files,
        IReadOnlyDictionary<string, RegularFileIdentity> Directories);

    private sealed record CleanupJournalEnvelope(
        string PayloadBase64,
        string Sha256);

    private bool TryRecoverJournaledDirectoryCleanup(
        string sourceRoot,
        out string reason)
    {
        reason = string.Empty;
        var normalizedSource = Path.GetFullPath(sourceRoot);
        var parentPath = Path.GetDirectoryName(normalizedSource);
        if (string.IsNullOrWhiteSpace(parentPath)
            || !Directory.Exists(parentPath))
        {
            return true;
        }

        try
        {
            using var parent =
                PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parentPath);
            CleanupJournalPayload? matching = null;
            string? matchingJournalName = null;
            foreach (var journalPath in Directory.EnumerateFiles(
                parentPath,
                $"{CopyCleanupMarker}*.journal",
                SearchOption.TopDirectoryOnly))
            {
                var journalName = Path.GetFileName(journalPath);
                using var journal = parent.OpenExistingFile(
                    journalName,
                    requireDeleteAccess: true);
                var payload = ReadCleanupJournalAsync(journal)
                    .GetAwaiter()
                    .GetResult();
                if (payload == null)
                {
                    reason =
                        "An invalid or modified directory-cleanup journal was preserved for operator review.";
                    return false;
                }
                if (string.Equals(
                        Path.GetFullPath(payload.SourceRoot),
                        normalizedSource,
                        OperatingSystem.IsWindows()
                            ? StringComparison.OrdinalIgnoreCase
                            : StringComparison.Ordinal))
                {
                    if (matching != null)
                    {
                        reason =
                            "Multiple cleanup journals claim the same source root.";
                        return false;
                    }
                    matching = payload;
                    matchingJournalName = journalName;
                }
            }

            if (matching == null)
            {
                return true;
            }
            return RecoverCleanupJournalAsync(
                    parent,
                    matchingJournalName!,
                    matching)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            reason =
                $"Directory-cleanup journal recovery failed: {exception.GetType().Name}.";
            return false;
        }
    }

    private async Task<bool> RecoverCleanupJournalAsync(
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string journalName,
        CleanupJournalPayload payload)
    {
        var expectedQuarantine =
            $"{CopyCleanupMarker}{payload.OperationId:N}.state";
        if (payload.Version != 1
            || !string.Equals(
                payload.QuarantineName,
                expectedQuarantine,
                StringComparison.Ordinal)
            || Path.GetDirectoryName(Path.GetFullPath(payload.SourceRoot))
                is not { } sourceParent
            || !string.Equals(
                Path.GetFullPath(sourceParent),
                Path.GetFullPath(parent.FullPath),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            return false;
        }
        if (Directory.Exists(payload.SourceRoot))
        {
            // The journal cannot authorize deleting a visible new generation.
            return false;
        }

        using var quarantinePublication =
            parent.TryOpenExistingChildForPublication(
                payload.QuarantineName);
        if (quarantinePublication == null)
        {
            return false;
        }
        using var quarantine =
            quarantinePublication.OpenCreatedDirectoryAnchor();
        if (!await ValidateCleanupManifestAsync(
                payload,
                quarantine.FullPath,
                validateDestination: true)
            || !await DeletePinnedCleanupTreeAsync(
                quarantine,
                payload,
                relativeDirectory: string.Empty))
        {
            return false;
        }
        quarantinePublication.DeletePinnedEmptyDirectory(
            payload.QuarantineName,
            immediateWindows: true);
        FlushFileMoveDirectory(
            parent,
            "recovered directory cleanup quarantine retirement");
        using var journal = parent.OpenExistingFile(
            journalName,
            requireDeleteAccess: true);
        journal.Delete(immediateWindows: true);
        FlushFileMoveDirectory(
            parent,
            "recovered directory cleanup journal retirement");
        return true;
    }

    private static async Task<CleanupJournalPayload?> ReadCleanupJournalAsync(
        PinnedDirectoryCreation.PinnedFileEntry journal)
    {
        await using var stream = journal.OpenReadStream(
            4096,
            asynchronous: false);
        if (stream.Length is <= 0 or > 4 * 1024 * 1024)
        {
            return null;
        }
        var bytes = new byte[stream.Length];
        var offset = 0;
        while (offset < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(offset));
            if (read == 0)
            {
                return null;
            }
            offset += read;
        }
        var envelope = JsonSerializer.Deserialize<CleanupJournalEnvelope>(bytes);
        if (envelope == null)
        {
            return null;
        }
        byte[] payloadBytes;
        try
        {
            payloadBytes = Convert.FromBase64String(envelope.PayloadBase64);
        }
        catch (FormatException)
        {
            return null;
        }
        if (!string.Equals(
                Convert.ToHexString(SHA256.HashData(payloadBytes)),
                envelope.Sha256,
                StringComparison.Ordinal))
        {
            return null;
        }
        var payload = JsonSerializer.Deserialize<CleanupJournalPayload>(
            payloadBytes);
        if (payload == null
            || payload.Version != 1
            || payload.OperationId == Guid.Empty
            || payload.Files == null
            || payload.Directories == null)
        {
            return null;
        }
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Files = payload.Files,
            Directories = payload.Directories
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToArray()
        });
        return string.Equals(
            Convert.ToHexString(SHA256.HashData(manifestBytes)),
            payload.ManifestHash,
            StringComparison.Ordinal)
            ? payload
            : null;
    }

    private async Task<DirectoryCopyCleanupResult> ExecuteJournaledDirectoryCleanupAsync(
        DirectoryCopySnapshot snapshot,
        string destinationRoot)
    {
        var normalizedDestination = Path.GetFullPath(destinationRoot);
        if (!TryGetDirectoryIdentity(normalizedDestination, out var destinationIdentity))
        {
            return new DirectoryCopyCleanupResult(
                false,
                false,
                "The copied destination root identity is unavailable.");
        }

        var operationId = Guid.NewGuid();
        var files = new List<CleanupJournalFile>(snapshot.Files.Count);
        foreach (var file in snapshot.Files)
        {
            var sourcePath = ResolveSnapshotPath(
                snapshot.SourceRoot,
                file.RelativePath,
                "journal source file");
            await using var stream = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var length = stream.Length;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            files.Add(new CleanupJournalFile(
                file.RelativePath,
                file.Identity,
                length,
                hash));
        }

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Files = files,
            Directories = snapshot.DirectoryIdentities
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToArray()
        });
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
        var quarantineName = $"{CopyCleanupMarker}{operationId:N}.state";
        var payload = new CleanupJournalPayload(
            Version: 1,
            operationId,
            Path.GetFullPath(snapshot.SourceRoot),
            normalizedDestination,
            snapshot.SourceRootIdentity,
            destinationIdentity,
            quarantineName,
            manifestHash,
            files,
            snapshot.DirectoryIdentities);
        var sourceParentPath = Path.GetDirectoryName(payload.SourceRoot)
            ?? throw new InvalidOperationException(
                "The copied source root has no parent directory.");
        var journalName = $"{CopyCleanupMarker}{operationId:N}.journal";

        try
        {
            using var sourceParent =
                PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                    sourceParentPath);
            using var sourcePublication =
                PinnedDirectoryCreation.OpenExistingForPublication(
                    sourceParentPath,
                    Path.GetFileName(payload.SourceRoot));
            using var sourceAnchor =
                sourcePublication.OpenCreatedDirectoryAnchor();
            using var sourceHandle = sourceAnchor.DuplicateHandleForOperation();
            if (!TryGetRegularFileIdentity(sourceHandle, out var pinnedRootIdentity)
                || pinnedRootIdentity != payload.SourceRootIdentity
                || !await ValidateCleanupManifestAsync(
                    payload,
                    payload.SourceRoot,
                    validateDestination: true))
            {
                return new DirectoryCopyCleanupResult(
                    true,
                    false,
                    "The source or destination changed before cleanup journaling.");
            }
            FlushFileMoveDirectory(
                sourceParent,
                "directory cleanup durability capability");

            using var journal = sourceParent.CreateNewFile(
                journalName,
                hiddenFile: true);
            await WriteCleanupJournalAsync(journal, payload);
            FlushFileMoveDirectory(
                sourceParent,
                "directory cleanup journal publication");

            using var quarantinePublication =
                sourcePublication.MovePinnedDirectoryTo(
                    sourceParent,
                    quarantineName);
            FlushFileMoveDirectory(
                sourceParent,
                "directory cleanup quarantine publication");
            using var quarantine =
                quarantinePublication.OpenCreatedDirectoryAnchor();
            if (!await ValidateCleanupManifestAsync(
                    payload,
                    quarantine.FullPath,
                    validateDestination: true))
            {
                return new DirectoryCopyCleanupResult(
                    true,
                    false,
                    "The quarantined source or copied destination changed; journal and quarantine were preserved.");
            }

            if (!await DeletePinnedCleanupTreeAsync(
                    quarantine,
                    payload,
                    relativeDirectory: string.Empty))
            {
                return new DirectoryCopyCleanupResult(
                    true,
                    false,
                    "The quarantined source changed during retirement; recovery evidence was preserved.");
            }
            quarantinePublication.DeletePinnedEmptyDirectory(
                quarantineName,
                immediateWindows: true);
            FlushFileMoveDirectory(
                sourceParent,
                "directory cleanup quarantine retirement");
            journal.Delete(immediateWindows: true);
            FlushFileMoveDirectory(
                sourceParent,
                "directory cleanup journal retirement");
            return new DirectoryCopyCleanupResult(true, true);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Preserved journaled directory cleanup state for {Source}",
                LogRedaction.SanitizeFilePath(snapshot.SourceRoot));
            return new DirectoryCopyCleanupResult(
                true,
                false,
                "Journaled source cleanup did not complete; recovery evidence was preserved.");
        }
    }
}
