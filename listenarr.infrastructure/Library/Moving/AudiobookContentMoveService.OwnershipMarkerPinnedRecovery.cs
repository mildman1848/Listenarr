using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static Task RetireCorruptOwnershipWriteAsync(
        string writePath,
        string markerPath,
        MarkerWriteIdentity expectedIdentity,
        Func<Task> authorizeMutation) =>
        RetirePinnedArtifactAsync(
            writePath,
            entry =>
            {
                var currentRead = ReadOwnershipMarkerResult(entry);
                if (currentRead.State == MarkerReadState.TemporarilyUnreadable)
                {
                    throw new IOException(
                        "A predecessor ownership-marker write file became temporarily unreadable and was preserved.",
                        currentRead.Error);
                }
                if (currentRead.State != MarkerReadState.CorruptOrTruncated
                    || !TryParseMarkerWriteIdentity(
                        entry.FullPath,
                        markerPath,
                        out var currentIdentity)
                    || currentIdentity != expectedIdentity)
                {
                    throw new MoveNeedsAttentionException(
                        "A truncated ownership-marker write file changed before cleanup.");
                }
            },
            authorizeMutation);

    private static Task RetireValidatedOwnershipWriteAsync(
        string writePath,
        MoveOwnershipMarker expected,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        FileSystemPathSemantics directorySemantics,
        Func<Task> authorizeMutation) =>
        RetirePinnedArtifactAsync(
            writePath,
            entry =>
            {
                var marker = ReadOwnershipMarker(entry, entry.FullPath);
                ValidateOwnershipMarker(
                    marker,
                    expected,
                    sourceSemantics,
                    targetSemantics,
                    directorySemantics);
            },
            authorizeMutation);

    private async Task<MoveOwnershipMarker> PublishRecoveredOwnershipWriteAsync(
        string markerPath,
        string writePath,
        MoveOwnershipMarker expected,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        FileSystemPathSemantics directorySemantics,
        Func<Task> authorizeMutation)
    {
        var markerDirectory = Path.GetDirectoryName(Path.GetFullPath(markerPath))
            ?? throw new MoveNeedsAttentionException(
                "The ownership marker directory is unavailable.");
        using var parent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
            markerDirectory);
        using var entry = parent.OpenExistingFile(
            Path.GetFileName(writePath),
            requireDeleteAccess: true);
        var marker = ReadOwnershipMarker(entry, writePath);
        ValidateOwnershipMarker(
            marker,
            expected,
            sourceSemantics,
            targetSemantics,
            directorySemantics);

        faultInjector?.OnOwnershipMarkerWrite(
            expected.JobId,
            GetOwnershipMarkerKind(expected.ArtifactType),
            OwnershipMarkerWriteFaultPoint.BeforeRecoveredPublication);
        await authorizeMutation();
        if (!parent.VisiblePathMatches() || !entry.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The validated ownership-marker write changed before publication.");
        }
        if (File.Exists(markerPath) || Directory.Exists(markerPath))
        {
            throw new MoveNeedsAttentionException(
                "The authoritative ownership marker appeared before publication.");
        }

        marker = ReadOwnershipMarker(entry, writePath);
        ValidateOwnershipMarker(
            marker,
            expected,
            sourceSemantics,
            targetSemantics,
            directorySemantics);
        entry.MoveWithinParent(Path.GetFileName(markerPath));
        return marker;
    }

    private static OwnershipMarkerKind GetOwnershipMarkerKind(string artifactType) =>
        artifactType switch
        {
            TemporaryDirectoryArtifactType => OwnershipMarkerKind.TemporaryDirectory,
            QuarantineDirectoryArtifactType => OwnershipMarkerKind.QuarantineDirectory,
            CleanupTombstoneArtifactType => OwnershipMarkerKind.CleanupTombstone,
            _ => throw new MoveNeedsAttentionException(
                "The ownership marker has an unsupported artifact type.")
        };
}
