namespace Listenarr.Infrastructure.Library.Moving;

internal static partial class PinnedLibraryDirectoryOwnershipMarker
{
    private static void RecoverConditionalReplacement(
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string fileName,
        Func<LibraryDirectoryOwnershipMarker.MarkerPayload, bool>
            isExpectedPredecessor,
        Func<LibraryDirectoryOwnershipMarker.MarkerPayload, bool>
            isPublishedGeneration)
    {
        var backupName =
            PinnedDirectoryCreation.GetConditionalReplacementBackupName(fileName);
        using var backup = parent.TryOpenExistingFile(
            backupName,
            requireDeleteAccess: true);
        if (backup == null)
        {
            return;
        }

        var backupPayload = LibraryDirectoryOwnershipMarker.ReadPayload(backup);
        if (!isExpectedPredecessor(backupPayload))
        {
            throw new InvalidOperationException(
                "The conditional marker replacement backup is unrelated.");
        }

        using var published = parent.TryOpenExistingFile(
            fileName,
            requireDeleteAccess: false);
        if (published == null)
        {
            backup.MoveWithinParent(fileName);
            parent.FlushDirectoryEntry();
            return;
        }

        var publishedPayload =
            LibraryDirectoryOwnershipMarker.ReadPayload(published);
        if (!isPublishedGeneration(publishedPayload))
        {
            throw new InvalidOperationException(
                "The marker destination changed while a predecessor backup remained.");
        }

        backup.Delete(immediateWindows: true);
        parent.FlushDirectoryEntry();
    }
}
