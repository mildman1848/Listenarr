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
        using var parentAnchor = new PinnedDirectoryAnchor(
            DuplicateSafeHandle(_parentHandle),
            _parentPath,
            followVisibleFinalLink: false);
        return PublishCreatedDirectoryTo(parentAnchor, finalName);
    }

    internal PinnedDirectoryAnchor PublishCreatedDirectoryTo(
        PinnedDirectoryAnchor destinationParent,
        string finalName)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destinationParent);
        ValidateLeafName(finalName);
        if (!Created || _directoryHandle == null || _directoryHandle.IsInvalid)
        {
            throw new InvalidOperationException(
                "A pinned directory handle is required for publication.");
        }
        if (!VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The prepared directory changed before publication.");
        }
        if (!destinationParent.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The destination parent changed before directory publication.");
        }

        using var destinationHandle = destinationParent.DuplicateHandleForOperation();
        RenameRelativeEntry(
            _parentHandle,
            _directoryHandle,
            _childName,
            destinationHandle,
            finalName);
        var publishedPath = Path.Join(destinationParent.FullPath, finalName);
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
