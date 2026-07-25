using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private sealed record PreparedOwnedCleanup(
        string DirectoryPath,
        string MarkerPath);

    private async Task<PreparedOwnedCleanup> PrepareOwnedDirectoryCleanupAsync(
        string originalDirectoryPath,
        string originalMarkerPath,
        MoveOwnershipMarker expectedTombstone,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        FileSystemPathSemantics directorySemantics,
        Func<Task> authorizeMutation)
    {
        var originalDirectory = Path.GetFullPath(originalDirectoryPath);
        var cleanupDirectory = Path.GetFullPath(expectedTombstone.DirectoryPath);
        var ownedArtifactType = expectedTombstone.OwnedArtifactType
            ?? throw new MoveNeedsAttentionException(
                "The cleanup tombstone has no owned artifact type.");
        var expectedOriginalDirectory = expectedTombstone.OwnedDirectoryPath
            ?? throw new MoveNeedsAttentionException(
                "The cleanup tombstone has no original owned directory identity.");

        try
        {
            if (!FileSystemPathIdentity.AreEquivalent(
                    originalDirectory,
                    expectedOriginalDirectory,
                    directorySemantics))
            {
                throw new MoveNeedsAttentionException(
                    "The cleanup tombstone does not match the original owned directory.");
            }
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            throw new MoveNeedsAttentionException(
                "The cleanup tombstone contains an invalid original directory identity.");
        }

        var parent = Path.GetDirectoryName(originalDirectory)
            ?? throw new MoveNeedsAttentionException(
                "The owned cleanup directory parent is unavailable.");
        ValidateExistingMoveDirectory(parent, "owned cleanup parent directory");
        if (!FileSystemSafety.TryValidateMutationTarget(
                cleanupDirectory,
                [parent],
                out cleanupDirectory,
                out var cleanupReason))
        {
            throw new MoveNeedsAttentionException(cleanupReason);
        }

        var cleanupMarkerPath = Path.Join(
            cleanupDirectory,
            Path.GetFileName(originalMarkerPath));
        var expectedDirectoryMarker = CreateOwnershipMarker(
            ownedArtifactType,
            expectedTombstone.JobId,
            expectedTombstone.Source,
            expectedTombstone.Target,
            originalDirectory);

        if (Directory.Exists(cleanupDirectory))
        {
            RejectRecreatedOriginalOwnedPath(expectedTombstone);

            ValidateExistingMoveDirectory(
                cleanupDirectory,
                "renamed owned cleanup directory");
            if (File.Exists(cleanupMarkerPath))
            {
                var marker = ReadOwnershipMarker(cleanupMarkerPath);
                ValidateOwnershipMarker(
                    marker,
                    expectedDirectoryMarker,
                    sourceSemantics,
                    targetSemantics,
                    directorySemantics);
            }

            return new PreparedOwnedCleanup(
                cleanupDirectory,
                cleanupMarkerPath);
        }

        if (File.Exists(cleanupDirectory))
        {
            throw new MoveNeedsAttentionException(
                "The renamed cleanup path is occupied by a file and was preserved.");
        }

        if (!Directory.Exists(originalDirectory))
        {
            if (File.Exists(originalDirectory))
            {
                throw new MoveNeedsAttentionException(
                    "The original owned directory path was replaced by a file and was preserved.");
            }

            return new PreparedOwnedCleanup(
                cleanupDirectory,
                cleanupMarkerPath);
        }

        ValidateExistingMoveDirectory(
            originalDirectory,
            "original owned cleanup directory");
        if (!File.Exists(originalMarkerPath))
        {
            throw new MoveNeedsAttentionException(
                "The original owned cleanup directory no longer has its ownership marker and was preserved.");
        }

        var originalMarker = ReadOwnershipMarker(originalMarkerPath);
        ValidateOwnershipMarker(
            originalMarker,
            expectedDirectoryMarker,
            sourceSemantics,
            targetSemantics,
            directorySemantics);
        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                originalDirectory,
                out _,
                out _,
                out var treeReason))
        {
            throw new MoveNeedsAttentionException(
                $"The owned directory could not be isolated safely: {treeReason}");
        }

        var markerKind = string.Equals(
            ownedArtifactType,
            TemporaryDirectoryArtifactType,
            StringComparison.Ordinal)
            ? OwnershipMarkerKind.TemporaryDirectory
            : OwnershipMarkerKind.QuarantineDirectory;
        faultInjector?.OnOwnershipCleanup(
            expectedTombstone.JobId,
            markerKind,
            OwnershipCleanupFaultPoint.BeforeCleanupDirectoryMove);

        var originalParent = Path.GetDirectoryName(originalDirectory)
            ?? throw new MoveNeedsAttentionException(
                "The original owned cleanup directory has no parent.");
        using (var publication = PinnedDirectoryCreation.OpenExistingForPublication(
            originalParent,
            Path.GetFileName(originalDirectory)))
        {
            using var originalAnchor = publication.OpenCreatedDirectoryAnchor();
            await authorizeMutation();
            ValidateExistingMoveDirectory(
                originalDirectory,
                "original owned cleanup directory");
            originalMarker = ReadOwnershipMarker(originalMarkerPath);
            ValidateOwnershipMarker(
                originalMarker,
                expectedDirectoryMarker,
                sourceSemantics,
                targetSemantics,
                directorySemantics);
            if (Directory.Exists(cleanupDirectory)
                || File.Exists(cleanupDirectory)
                || !originalAnchor.VisiblePathMatches())
            {
                throw new MoveNeedsAttentionException(
                    "The renamed cleanup path appeared or the owned directory changed before isolation.");
            }

            using var cleanupAnchor = publication.RepublishPinnedDirectory(
                Path.GetFileName(originalDirectory),
                Path.GetFileName(cleanupDirectory));
        }

        ValidateExistingMoveDirectory(
            cleanupDirectory,
            "renamed owned cleanup directory");
        var movedMarker = ReadOwnershipMarker(cleanupMarkerPath);
        ValidateOwnershipMarker(
            movedMarker,
            expectedDirectoryMarker,
            sourceSemantics,
            targetSemantics,
            directorySemantics);
        return new PreparedOwnedCleanup(
            cleanupDirectory,
            cleanupMarkerPath);
    }

    private static void RejectRecreatedOriginalOwnedPath(
        MoveOwnershipMarker expectedTombstone)
    {
        var originalDirectory = expectedTombstone.OwnedDirectoryPath
            ?? throw new MoveNeedsAttentionException(
                "The cleanup tombstone has no original owned directory identity.");
        if (Directory.Exists(originalDirectory) || File.Exists(originalDirectory))
        {
            throw new MoveNeedsAttentionException(
                "The original owned directory path was recreated after cleanup isolation and was preserved.");
        }
    }
}
