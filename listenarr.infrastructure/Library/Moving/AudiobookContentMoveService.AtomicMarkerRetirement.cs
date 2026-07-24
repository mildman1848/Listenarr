namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task RetireSourceAtomicMarkerBeforeCopyFallbackAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string markerPath,
        CancellationToken cancellationToken)
    {
        using var sourceAnchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(source);
        using var markerEntry = sourceAnchor.OpenExistingFile(
            Path.GetFileName(markerPath),
            requireDeleteAccess: true);
        if (!sourceAnchor.VisiblePathMatches()
            || !markerEntry.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The source-side atomic recovery marker changed before copy fallback.");
        }

        ValidatePinnedAtomicMarker(
            markerEntry,
            markerPath,
            request,
            source,
            target);
        await EnsureMutationAuthorizedAsync(
            request,
            source,
            target,
            cancellationToken);
        if (!sourceAnchor.VisiblePathMatches()
            || !markerEntry.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The source-side atomic recovery marker changed at copy fallback.");
        }

        ValidatePinnedAtomicMarker(
            markerEntry,
            markerPath,
            request,
            source,
            target);
        markerEntry.Delete();
    }

    private void ValidatePinnedAtomicMarker(
        PinnedDirectoryCreation.PinnedFileEntry markerEntry,
        string markerPath,
        AudiobookContentMoveRequest request,
        string source,
        string target)
    {
        var parsed = ReadRecoveryMarker(markerEntry, markerPath);
        ValidateRecoveryMarker(parsed, request, source, target);
        if (!CanAdvanceRecoveryStage(parsed.Stage, AtomicRenameCompletedStage))
        {
            throw new MoveNeedsAttentionException(
                "The source-side recovery marker is not an authoritative atomic marker.");
        }
    }
}
