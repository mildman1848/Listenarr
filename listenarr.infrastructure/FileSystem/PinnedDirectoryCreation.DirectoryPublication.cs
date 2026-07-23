namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal static PinnedDirectoryCreation OpenExistingForPublication(
        string parentPath,
        string childName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parentPath);
        ValidateLeafName(childName);
        ExclusiveDirectoryCreator.InvokeBeforeOpenParentHook(parentPath);

        var parentHandle = OperatingSystem.IsWindows()
            ? OpenDirectoryWindows(parentPath, openReparsePoint: true)
            : OpenDirectoryUnix(parentPath, noFollow: true);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                EnsureWindowsParentIsNotReparsePoint(parentHandle, parentPath);
            }

            var childPath = Path.Join(parentPath, childName);
            var directoryHandle = OperatingSystem.IsWindows()
                ? OpenRelativeDirectoryWindows(
                    parentHandle,
                    childName,
                    childPath,
                    requireDeleteAccess: true)
                : OpenDirectoryAtUnix(parentHandle, childName);
            var publication = new PinnedDirectoryCreation(
                parentHandle,
                directoryHandle,
                parentPath,
                childName,
                created: true);
            if (publication.VisiblePathMatches())
            {
                return publication;
            }

            publication.Dispose();
            throw new InvalidOperationException(
                "The existing directory changed while it was being pinned for publication.");
        }
        catch
        {
            parentHandle.Dispose();
            throw;
        }
    }

    internal PinnedDirectoryAnchor PublishCreatedDirectoryAs(string finalName)
    {
        ThrowIfDisposed();
        ValidateLeafName(finalName);
        if (!Created || _directoryHandle == null || _directoryHandle.IsInvalid)
        {
            throw new InvalidOperationException(
                "A pinned directory handle is required for sibling publication.");
        }
        if (!VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The prepared directory changed before sibling publication.");
        }

        RenameRelativeEntry(
            _parentHandle,
            _directoryHandle,
            _childName,
            finalName);
        var publishedPath = Path.Join(_parentPath, finalName);
        var publishedAnchor = new PinnedDirectoryAnchor(
            DuplicateSafeHandle(_directoryHandle),
            publishedPath,
            followVisibleFinalLink: false);
        if (publishedAnchor.VisiblePathMatches())
        {
            return publishedAnchor;
        }

        publishedAnchor.Dispose();
        throw new InvalidOperationException(
            "The published directory does not identify the prepared pinned directory.");
    }
}
