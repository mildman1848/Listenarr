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
                created: true,
                parentFollowsVisibleFinalLink: false);
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
            _parentFollowsVisibleFinalLink);
        return PublishCreatedDirectoryTo(parentAnchor, finalName);
    }

    internal PinnedDirectoryAnchor RepublishPinnedDirectory(
        string currentName,
        string finalName)
    {
        ThrowIfDisposed();
        ValidateLeafName(currentName);
        ValidateLeafName(finalName);
        if (!Created || _directoryHandle == null || _directoryHandle.IsInvalid)
        {
            throw new InvalidOperationException(
                "A pinned directory handle is required for publication.");
        }

        var currentPath = Path.Join(_parentPath, currentName);
        using var currentAnchor = new PinnedDirectoryAnchor(
            DuplicateSafeHandle(_directoryHandle),
            currentPath,
            followVisibleFinalLink: false);
        if (!currentAnchor.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The current directory path no longer identifies the pinned directory.");
        }

        RenameRelativeEntry(
            _parentHandle,
            _directoryHandle,
            currentName,
            _parentHandle,
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
            "The republished directory does not identify the pinned directory.");
    }

    internal void DeletePinnedEmptyDirectory(
        string currentName,
        bool immediateWindows = false)
    {
        ThrowIfDisposed();
        ValidateLeafName(currentName);
        if (!Created || _directoryHandle == null || _directoryHandle.IsInvalid)
        {
            throw new InvalidOperationException(
                "A pinned directory handle is required for deletion.");
        }

        var currentPath = Path.Join(_parentPath, currentName);
        using var currentAnchor = new PinnedDirectoryAnchor(
            DuplicateSafeHandle(_directoryHandle),
            currentPath,
            followVisibleFinalLink: false);
        if (!currentAnchor.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The directory changed before pinned deletion.");
        }
        if (OperatingSystem.IsWindows())
        {
            if (immediateWindows)
            {
                DeleteOpenedFileImmediatelyWindows(_directoryHandle);
            }
            else
            {
                DeleteOpenedFileWindows(_directoryHandle);
            }
            return;
        }

        var retiredName = $".listenarr-retired-directory-{Guid.NewGuid():N}.state";
        RenameRelativeEntry(
            _parentHandle,
            _directoryHandle,
            currentName,
            _parentHandle,
            retiredName);
        using var reopened = OpenDirectoryAtUnix(_parentHandle, retiredName);
        if (!HandlesIdentifySameDirectory(_directoryHandle, reopened))
        {
            throw new InvalidOperationException(
                "The retired directory no longer identifies the pinned directory.");
        }

        var flags = OperatingSystem.IsMacOS() ? AtRemovedirMac : AtRemovedirLinux;
        if (UnlinkAt(
                _parentHandle.DangerousGetHandle().ToInt32(),
                retiredName,
                flags) != 0)
        {
            throw new System.ComponentModel.Win32Exception(
                System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                "Could not remove the verified empty directory.");
        }
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

    internal PinnedDirectoryCreation MovePinnedDirectoryTo(
        PinnedDirectoryAnchor destinationParent,
        string finalName)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(destinationParent);
        ValidateLeafName(finalName);
        if (!Created || _directoryHandle == null || _directoryHandle.IsInvalid)
        {
            throw new InvalidOperationException(
                "A pinned directory handle is required for relocation.");
        }
        if (!VisiblePathMatches() || !destinationParent.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "A directory relocation endpoint changed before publication.");
        }

        var destinationHandle = destinationParent.DuplicateHandleForOperation();
        try
        {
            RenameRelativeEntry(
                _parentHandle,
                _directoryHandle,
                _childName,
                destinationHandle,
                finalName);
            var relocated = new PinnedDirectoryCreation(
                destinationHandle,
                DuplicateSafeHandle(_directoryHandle),
                destinationParent.FullPath,
                finalName,
                created: true,
                destinationParent.FollowsVisibleFinalLink);
            if (relocated.VisiblePathMatches())
            {
                return relocated;
            }

            relocated.Dispose();
            throw new InvalidOperationException(
                "The relocated directory does not identify the pinned directory.");
        }
        catch
        {
            destinationHandle.Dispose();
            throw;
        }
    }
}
