namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private sealed class QuarantinedEmptyDestinationPlaceholder : IDisposable
    {
        private readonly PinnedDirectoryCreation.PinnedDirectoryAnchor _anchor;
        private bool _completed;

        internal QuarantinedEmptyDestinationPlaceholder(
            PinnedDirectoryCreation.PinnedDirectoryAnchor anchor,
            string originalPath,
            string quarantinePath)
        {
            _anchor = anchor;
            OriginalPath = originalPath;
            QuarantinePath = quarantinePath;
        }

        internal string OriginalPath { get; }

        internal string QuarantinePath { get; }

        internal bool TryDeleteAfterPublication(out string reason)
        {
            reason = string.Empty;
            if (_completed)
            {
                return true;
            }

            if (!_anchor.VisiblePathMatches(QuarantinePath))
            {
                reason = "The quarantined empty destination no longer identifies the pinned placeholder.";
                return false;
            }

            if (!TryVerifyEmptyDirectory(QuarantinePath, out reason))
            {
                return false;
            }

            try
            {
                Directory.Delete(QuarantinePath);
                if (Directory.Exists(QuarantinePath))
                {
                    reason = "The quarantined empty destination still exists after cleanup.";
                    return false;
                }

                _completed = true;
                return true;
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                reason = $"The quarantined empty destination could not be removed: {exception.GetType().Name}.";
                return false;
            }
        }

        internal bool TryRestore(out string reason)
        {
            reason = string.Empty;
            if (_completed)
            {
                return true;
            }

            if (Directory.Exists(OriginalPath))
            {
                reason = "The original destination path was occupied before its empty placeholder could be restored.";
                return false;
            }

            if (!_anchor.VisiblePathMatches(QuarantinePath))
            {
                reason = "The quarantined empty destination no longer identifies the pinned placeholder.";
                return false;
            }

            if (!TryVerifyEmptyDirectory(QuarantinePath, out reason))
            {
                return false;
            }

            try
            {
                Directory.Move(QuarantinePath, OriginalPath);
                if (!_anchor.VisiblePathMatches(OriginalPath))
                {
                    reason = "The restored destination does not identify the pinned empty placeholder.";
                    return false;
                }

                _completed = true;
                return true;
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                reason = $"The empty destination placeholder could not be restored: {exception.GetType().Name}.";
                return false;
            }
        }

        public void Dispose() => _anchor.Dispose();
    }

    private async Task<(
        bool Success,
        QuarantinedEmptyDestinationPlaceholder? Placeholder,
        string? Reason)> PrepareMoveDestinationAsync(
            DirectoryCopySnapshot snapshot,
            string destinationPath)
    {
        if (!Directory.Exists(destinationPath))
        {
            return (true, null, null);
        }

        if (await DirectoryCopySnapshotExactlyMatchesAsync(snapshot, destinationPath))
        {
            return (true, null, null);
        }

        if (!TryVerifyEmptyDirectory(destinationPath, out var emptyReason))
        {
            return (
                false,
                null,
                $"The existing destination is not a safe empty placeholder: {emptyReason}");
        }

        PinnedDirectoryCreation.PinnedDirectoryAnchor? anchor = null;
        string? quarantinePath = null;
        var movedToQuarantine = false;
        try
        {
            anchor = PinnedDirectoryCreation.OpenPinnedVisibleDirectory(destinationPath);
            if (!anchor.VisiblePathMatches()
                || !TryVerifyEmptyDirectory(destinationPath, out emptyReason))
            {
                return (
                    false,
                    null,
                    $"The empty destination changed while it was being pinned: {emptyReason}");
            }

            if (BeforeEmptyDestinationPlaceholderQuarantineForTestAsync != null)
            {
                await BeforeEmptyDestinationPlaceholderQuarantineForTestAsync(destinationPath);
            }

            if (!anchor.VisiblePathMatches()
                || !TryVerifyEmptyDirectory(destinationPath, out emptyReason))
            {
                return (
                    false,
                    null,
                    $"The empty destination changed before quarantine: {emptyReason}");
            }

            var parent = Path.GetDirectoryName(destinationPath);
            var leaf = Path.GetFileName(destinationPath);
            if (string.IsNullOrWhiteSpace(parent)
                || string.IsNullOrWhiteSpace(leaf)
                || !Directory.Exists(parent)
                || IsLinkedOrUnverifiableEntry(parent))
            {
                return (
                    false,
                    null,
                    "The empty destination parent could not be proven safe for quarantine.");
            }

            quarantinePath = Path.Join(
                parent,
                $".{leaf}.listenarr-empty-placeholder-{Guid.NewGuid():N}");
            Directory.Move(destinationPath, quarantinePath);
            movedToQuarantine = true;
            if (!anchor.VisiblePathMatches(quarantinePath)
                || !TryVerifyEmptyDirectory(quarantinePath, out emptyReason))
            {
                TryRestoreMovedEntry(quarantinePath, destinationPath, out _);
                movedToQuarantine = false;
                return (
                    false,
                    null,
                    $"The quarantined destination did not match the pinned empty placeholder: {emptyReason}");
            }

            var placeholder = new QuarantinedEmptyDestinationPlaceholder(
                anchor,
                destinationPath,
                quarantinePath);
            anchor = null;
            movedToQuarantine = false;
            return (true, placeholder, null);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            if (movedToQuarantine && quarantinePath != null)
            {
                TryRestoreMovedEntry(quarantinePath, destinationPath, out _);
            }

            return (
                false,
                null,
                $"The empty destination placeholder could not be quarantined: {exception.GetType().Name}.");
        }
        finally
        {
            anchor?.Dispose();
        }
    }

    private static bool TryVerifyEmptyDirectory(string path, out string reason)
    {
        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                path,
                out var files,
                out var directories,
                out reason))
        {
            return false;
        }

        if (files.Count != 0 || directories.Count != 0)
        {
            reason = "The directory contains files or child directories.";
            return false;
        }

        return true;
    }

    private static bool TryRestoreMovedEntry(
        string quarantinePath,
        string originalPath,
        out string reason)
    {
        reason = string.Empty;
        try
        {
            if (!Directory.Exists(quarantinePath))
            {
                reason = "The quarantined entry no longer exists.";
                return false;
            }

            if (Directory.Exists(originalPath))
            {
                reason = "The original destination path is already occupied.";
                return false;
            }

            Directory.Move(quarantinePath, originalPath);
            return true;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            reason = $"The moved destination entry could not be restored: {exception.GetType().Name}.";
            return false;
        }
    }
}
