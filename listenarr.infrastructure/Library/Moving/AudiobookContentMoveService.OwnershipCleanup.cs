using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task DeleteOwnedDirectoryWithTombstoneAsync(
        string directoryPath,
        string markerPath,
        string ownedArtifactType,
        Guid jobId,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        FileSystemPathSemantics directorySemantics,
        MoveLeaseToken leaseToken,
        Func<Task> authorizeMutation)
    {
        var fullDirectory = Path.GetFullPath(directoryPath);
        var cleanupDirectory = GetCleanupDirectoryPath(
            fullDirectory,
            ownedArtifactType,
            jobId);
        var tombstonePath = GetCleanupTombstonePath(
            fullDirectory,
            ownedArtifactType,
            jobId);
        var expectedTombstone = CreateOwnershipMarker(
            CleanupTombstoneArtifactType,
            jobId,
            source,
            target,
            cleanupDirectory,
            ownedArtifactType,
            fullDirectory);

        await EnsureCleanupTombstoneAsync(
            tombstonePath,
            expectedTombstone,
            sourceSemantics,
            targetSemantics,
            directorySemantics,
            leaseToken,
            authorizeMutation);
        var prepared = await PrepareOwnedDirectoryCleanupAsync(
            fullDirectory,
            markerPath,
            expectedTombstone,
            sourceSemantics,
            targetSemantics,
            directorySemantics,
            authorizeMutation);
        await CompleteOwnedDirectoryCleanupAsync(
            prepared.DirectoryPath,
            prepared.MarkerPath,
            tombstonePath,
            expectedTombstone,
            sourceSemantics,
            targetSemantics,
            directorySemantics,
            authorizeMutation);
    }

    private async Task<bool> TryCompleteOwnedDirectoryCleanupAsync(
        string directoryPath,
        string markerPath,
        string ownedArtifactType,
        Guid jobId,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        FileSystemPathSemantics directorySemantics,
        MoveLeaseToken leaseToken,
        Func<Task> authorizeMutation)
    {
        var fullDirectory = Path.GetFullPath(directoryPath);
        var cleanupDirectory = GetCleanupDirectoryPath(
            fullDirectory,
            ownedArtifactType,
            jobId);
        var tombstonePath = GetCleanupTombstonePath(
            fullDirectory,
            ownedArtifactType,
            jobId);
        var tombstoneWritePrefix = Path.GetFileName(tombstonePath) + ".writing-";
        var parent = Path.GetDirectoryName(tombstonePath)
            ?? throw new MoveNeedsAttentionException("The cleanup tombstone parent is unavailable.");
        var hasTombstoneEvidence = File.Exists(tombstonePath)
            || Directory.EnumerateFiles(
                parent,
                tombstoneWritePrefix + "*",
                SearchOption.TopDirectoryOnly).Any();
        if (!hasTombstoneEvidence)
        {
            return false;
        }

        var expectedTombstone = CreateOwnershipMarker(
            CleanupTombstoneArtifactType,
            jobId,
            source,
            target,
            cleanupDirectory,
            ownedArtifactType,
            fullDirectory);
        await authorizeMutation();
        try
        {
            await RecoverOrReadOwnershipMarkerAsync(
                tombstonePath,
                expectedTombstone,
                sourceSemantics,
                targetSemantics,
                directorySemantics,
                leaseToken,
                authorizeMutation);
        }
        catch (InterruptedOwnershipPublicationException)
        {
            return false;
        }

        var prepared = await PrepareOwnedDirectoryCleanupAsync(
            fullDirectory,
            markerPath,
            expectedTombstone,
            sourceSemantics,
            targetSemantics,
            directorySemantics,
            authorizeMutation);
        await CompleteOwnedDirectoryCleanupAsync(
            prepared.DirectoryPath,
            prepared.MarkerPath,
            tombstonePath,
            expectedTombstone,
            sourceSemantics,
            targetSemantics,
            directorySemantics,
            authorizeMutation);
        return true;
    }

    private async Task EnsureCleanupTombstoneAsync(
        string tombstonePath,
        MoveOwnershipMarker expectedTombstone,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        FileSystemPathSemantics directorySemantics,
        MoveLeaseToken leaseToken,
        Func<Task> authorizeMutation)
    {
        var parent = Path.GetDirectoryName(tombstonePath)
            ?? throw new MoveNeedsAttentionException("The cleanup tombstone parent is unavailable.");
        var hasPublicationEvidence = File.Exists(tombstonePath)
            || Directory.EnumerateFiles(
                parent,
                Path.GetFileName(tombstonePath) + ".writing-*",
                SearchOption.TopDirectoryOnly).Any();
        if (!hasPublicationEvidence)
        {
            await authorizeMutation();
            await PublishOwnershipMarkerAsync(
                tombstonePath,
                expectedTombstone,
                OwnershipMarkerKind.CleanupTombstone,
                leaseToken,
                authorizeMutation);
        }

        await authorizeMutation();
        try
        {
            await RecoverOrReadOwnershipMarkerAsync(
                tombstonePath,
                expectedTombstone,
                sourceSemantics,
                targetSemantics,
                directorySemantics,
                leaseToken,
                authorizeMutation);
        }
        catch (InterruptedOwnershipPublicationException)
        {
            await authorizeMutation();
            await PublishOwnershipMarkerAsync(
                tombstonePath,
                expectedTombstone,
                OwnershipMarkerKind.CleanupTombstone,
                leaseToken,
                authorizeMutation);
            await RecoverOrReadOwnershipMarkerAsync(
                tombstonePath,
                expectedTombstone,
                sourceSemantics,
                targetSemantics,
                directorySemantics,
                leaseToken,
                authorizeMutation);
        }
    }

    private async Task CompleteOwnedDirectoryCleanupAsync(
        string directoryPath,
        string markerPath,
        string tombstonePath,
        MoveOwnershipMarker expectedTombstone,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        FileSystemPathSemantics directorySemantics,
        Func<Task> authorizeMutation)
    {
        var markerKind = string.Equals(
            expectedTombstone.OwnedArtifactType,
            TemporaryDirectoryArtifactType,
            StringComparison.Ordinal)
            ? OwnershipMarkerKind.TemporaryDirectory
            : OwnershipMarkerKind.QuarantineDirectory;
        var tombstoneParent = Path.GetDirectoryName(Path.GetFullPath(tombstonePath))
            ?? throw new MoveNeedsAttentionException("The cleanup tombstone parent is unavailable.");
        var originalOwnedDirectory = expectedTombstone.OwnedDirectoryPath
            ?? throw new MoveNeedsAttentionException(
                "The cleanup tombstone has no original owned directory identity.");
        var expectedDirectoryMarker = CreateOwnershipMarker(
            expectedTombstone.OwnedArtifactType
                ?? throw new MoveNeedsAttentionException("The cleanup tombstone has no owned artifact type."),
            expectedTombstone.JobId,
            expectedTombstone.Source,
            expectedTombstone.Target,
            originalOwnedDirectory);
        try
        {
            if (!FileSystemPathIdentity.AreEquivalent(
                    directoryPath,
                    expectedTombstone.DirectoryPath,
                    directorySemantics))
            {
                throw new MoveNeedsAttentionException(
                    "The cleanup directory does not match the persisted tombstone identity.");
            }
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            throw new MoveNeedsAttentionException(
                "The cleanup tombstone contains an invalid cleanup directory identity.");
        }
        ValidateExistingMoveDirectory(tombstoneParent, "cleanup tombstone directory");
        var tombstone = ReadOwnershipMarker(tombstonePath);
        ValidateOwnershipMarker(
            tombstone,
            expectedTombstone,
            sourceSemantics,
            targetSemantics,
            directorySemantics);

        if (TryGetExistingPathAttributes(directoryPath, out var ownedPathAttributes)
            && (ownedPathAttributes & FileAttributes.Directory) == 0)
        {
            throw new MoveNeedsAttentionException(
                "The tombstoned owned directory path is occupied by a file and was preserved for operator review.");
        }

        if (Directory.Exists(directoryPath))
        {
            ValidateExistingMoveDirectory(directoryPath, "owned cleanup directory");
            if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                    directoryPath,
                    out var files,
                    out var directories,
                    out var reason))
            {
                throw new MoveNeedsAttentionException(
                    $"The owned directory could not be cleaned safely: {reason}");
            }

            var hasDirectoryMarker = File.Exists(markerPath);
            MoveOwnershipMarker? directoryMarker = null;
            if (hasDirectoryMarker)
            {
                ValidateOwnedCleanupEntry(markerPath, directoryPath);
                directoryMarker = ReadOwnershipMarker(markerPath);
                ValidateOwnershipMarker(
                    directoryMarker,
                    expectedDirectoryMarker,
                    sourceSemantics,
                    targetSemantics,
                    directorySemantics);
            }

            var ownedFiles = files
                .Where(file => !FileSystemPathIdentity.AreEquivalent(
                    file,
                    markerPath,
                    directorySemantics))
                .ToList();
            if (markerKind == OwnershipMarkerKind.QuarantineDirectory
                && ownedFiles.Count > 0)
            {
                throw new MoveNeedsAttentionException(
                    "The quarantine cleanup directory contains unexpected content and was preserved.");
            }

            if (!hasDirectoryMarker
                && (ownedFiles.Count > 0 || directories.Count > 0))
            {
                throw new MoveNeedsAttentionException(
                    "The tombstoned cleanup directory was recreated or changed after its ownership marker was removed.");
            }

            foreach (var file in ownedFiles)
            {
                await RetirePinnedArtifactAsync(
                    file,
                    _ => ValidateOwnedCleanupEntry(file, directoryPath),
                    authorizeMutation);
            }

            foreach (var directory in directories.OrderByDescending(path => path.Length))
            {
                ValidateOwnedCleanupEntry(directory, directoryPath);
                if (Directory.Exists(directory)
                    && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    await authorizeMutation();
                    ValidateOwnedCleanupEntry(directory, directoryPath);
                    Directory.Delete(directory, recursive: false);
                }
            }

            if (hasDirectoryMarker)
            {
                faultInjector?.OnOwnershipCleanup(
                    expectedTombstone.JobId,
                    markerKind,
                    OwnershipCleanupFaultPoint.BeforeOwnershipMarkerDelete);
                ValidateExistingMoveDirectory(directoryPath, "owned cleanup directory");
                ValidateOwnedCleanupEntry(markerPath, directoryPath);
                directoryMarker = ReadOwnershipMarker(markerPath);
                ValidateOwnershipMarker(
                    directoryMarker,
                    expectedDirectoryMarker,
                    sourceSemantics,
                    targetSemantics,
                    directorySemantics);
                await RetirePinnedArtifactAsync(
                    markerPath,
                    entry =>
                    {
                        ValidateOwnedCleanupEntry(markerPath, directoryPath);
                        directoryMarker = ReadOwnershipMarker(entry, markerPath);
                        ValidateOwnershipMarker(
                            directoryMarker,
                            expectedDirectoryMarker,
                            sourceSemantics,
                            targetSemantics,
                            directorySemantics);
                    },
                    authorizeMutation);
            }

            ValidateExistingMoveDirectory(directoryPath, "owned cleanup directory");
            if (Directory.EnumerateFileSystemEntries(directoryPath).Any())
            {
                throw new MoveNeedsAttentionException(
                    "The owned directory still contains unexpected content after cleanup.");
            }

            faultInjector?.OnOwnershipCleanup(
                expectedTombstone.JobId,
                markerKind,
                OwnershipCleanupFaultPoint.BeforeDirectoryDelete);
            ValidateExistingMoveDirectory(directoryPath, "owned cleanup directory");
            if (Directory.EnumerateFileSystemEntries(directoryPath).Any())
            {
                throw new MoveNeedsAttentionException(
                    "The owned directory changed before final deletion.");
            }

            await authorizeMutation();
            RejectRecreatedOriginalOwnedPath(expectedTombstone);
            ValidateExistingMoveDirectory(directoryPath, "owned cleanup directory");
            if (Directory.EnumerateFileSystemEntries(directoryPath).Any())
            {
                throw new MoveNeedsAttentionException(
                    "The owned cleanup directory changed during final authorization.");
            }

            Directory.Delete(directoryPath, recursive: false);
        }

        ValidateExistingMoveDirectory(tombstoneParent, "cleanup tombstone directory");
        var validatedTombstone = ReadOwnershipMarker(tombstonePath);
        ValidateOwnershipMarker(
            validatedTombstone,
            expectedTombstone,
            sourceSemantics,
            targetSemantics,
            directorySemantics);
        faultInjector?.OnOwnershipCleanup(
            expectedTombstone.JobId,
            markerKind,
            OwnershipCleanupFaultPoint.BeforeTombstoneDelete);
        ValidateExistingMoveDirectory(tombstoneParent, "cleanup tombstone directory");
        validatedTombstone = ReadOwnershipMarker(tombstonePath);
        ValidateOwnershipMarker(
            validatedTombstone,
            expectedTombstone,
            sourceSemantics,
            targetSemantics,
            directorySemantics);
        await RetirePinnedArtifactAsync(
            tombstonePath,
            entry =>
            {
                RejectRecreatedOriginalOwnedPath(expectedTombstone);
                validatedTombstone = ReadOwnershipMarker(entry, tombstonePath);
                ValidateOwnershipMarker(
                    validatedTombstone,
                    expectedTombstone,
                    sourceSemantics,
                    targetSemantics,
                    directorySemantics);
            },
            authorizeMutation);
    }

    private static bool TryGetExistingPathAttributes(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static void ValidateOwnedCleanupEntry(
        string entryPath,
        string directoryPath)
    {
        if (!FileSystemSafety.TryValidateMutationTarget(
                entryPath,
                [directoryPath],
                out entryPath,
                out var reason))
        {
            throw new MoveNeedsAttentionException(reason);
        }

        if ((File.Exists(entryPath) || Directory.Exists(entryPath))
            && (File.GetAttributes(entryPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                "An owned cleanup entry is a symbolic link or reparse point.");
        }
    }
}
