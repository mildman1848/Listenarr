namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static bool TryReconcileCleanupJournalReplacements(
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string parentPath,
        out string reason)
    {
        reason = string.Empty;
        const string predecessorSuffix = ".listenarr-predecessor.tmp";
        foreach (var backupPath in Directory.EnumerateFiles(
            parentPath,
            $"{CopyCleanupMarker}*.journal{predecessorSuffix}",
            SearchOption.TopDirectoryOnly))
        {
            var backupName = Path.GetFileName(backupPath);
            if (!backupName.EndsWith(
                    predecessorSuffix,
                    StringComparison.Ordinal))
            {
                reason = "A cleanup-journal predecessor has an invalid name.";
                return false;
            }

            var journalName = backupName[..^predecessorSuffix.Length];
            if (!journalName.StartsWith(
                    CopyCleanupMarker,
                    StringComparison.Ordinal)
                || !journalName.EndsWith(
                    ".journal",
                    StringComparison.Ordinal))
            {
                reason = "A cleanup-journal predecessor is not attributable to a cleanup journal.";
                return false;
            }

            using var predecessor = parent.OpenExistingFile(
                backupName,
                requireDeleteAccess: true);
            using var published = parent.TryOpenExistingFile(
                journalName,
                requireDeleteAccess: true);
            if (published == null)
            {
                if (!predecessor.VisiblePathMatches())
                {
                    reason = "A cleanup-journal predecessor changed before restoration.";
                    return false;
                }

                predecessor.MoveWithinParent(journalName);
                parent.FlushDirectoryEntry();
                continue;
            }

            var predecessorPayload = ReadCleanupJournalAsync(predecessor)
                .GetAwaiter()
                .GetResult();
            var publishedPayload = ReadCleanupJournalAsync(published)
                .GetAwaiter()
                .GetResult();
            if (predecessorPayload is not { Version: 1 }
                || publishedPayload is not { Version: 2 }
                || predecessorPayload.OperationId
                    != publishedPayload.OperationId
                || !string.Equals(
                    predecessorPayload.SourceRoot,
                    publishedPayload.SourceRoot,
                    StringComparison.Ordinal)
                || !string.Equals(
                    predecessorPayload.DestinationRoot,
                    publishedPayload.DestinationRoot,
                    StringComparison.Ordinal)
                || !string.Equals(
                    predecessorPayload.QuarantineName,
                    publishedPayload.QuarantineName,
                    StringComparison.Ordinal)
                || !predecessor.VisiblePathMatches()
                || !published.VisiblePathMatches())
            {
                reason = "A cleanup-journal replacement could not be reconciled safely.";
                return false;
            }

            predecessor.Delete(immediateWindows: true);
            parent.FlushDirectoryEntry();
        }

        return true;
    }
}
