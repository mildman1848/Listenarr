namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal PinnedDirectoryAnchor OpenParentDirectoryAnchor()
    {
        ThrowIfDisposed();
        var anchor = new PinnedDirectoryAnchor(
            DuplicateSafeHandle(_parentHandle),
            _parentPath,
            followVisibleFinalLink: false);
        if (anchor.VisiblePathMatches())
        {
            return anchor;
        }

        anchor.Dispose();
        throw new InvalidOperationException(
            "The pinned directory parent changed while it was being opened.");
    }

    internal sealed partial class PinnedDirectoryAnchor
    {
        internal PinnedDirectoryCreation OpenExistingChildForPublication(
            string childName)
        {
            ThrowIfDisposed();
            ValidateLeafName(childName);
            EnsureVisiblePathMatches();
            var childPath = Path.Join(FullPath, childName);
            var directoryHandle = OperatingSystem.IsWindows()
                ? OpenRelativeDirectoryWindows(
                    _handle,
                    childName,
                    childPath,
                    requireDeleteAccess: true)
                : OpenDirectoryAtUnix(_handle, childName);
            var publication = new PinnedDirectoryCreation(
                DuplicateSafeHandle(_handle),
                directoryHandle,
                FullPath,
                childName,
                created: true);
            if (publication.VisiblePathMatches())
            {
                return publication;
            }

            publication.Dispose();
            throw new InvalidOperationException(
                "The existing child changed while it was being pinned for publication.");
        }
    }
}
