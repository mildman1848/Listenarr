using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private bool? TryPinnedSameVolumeDirectoryMove(
        string sourceDirectory,
        string destinationDirectory)
    {
        try
        {
            var source = Path.GetFullPath(sourceDirectory);
            var destination = Path.GetFullPath(destinationDirectory);
            var sourceParentPath = Path.GetDirectoryName(source)
                ?? throw new InvalidOperationException(
                    "The source directory has no parent.");
            var destinationParentPath = Path.GetDirectoryName(destination)
                ?? throw new InvalidOperationException(
                    "The destination directory has no parent.");
            using var sourceParent =
                PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    sourceParentPath,
                    createMissing: false);
            using var destinationParent =
                PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(
                    destinationParentPath,
                    createMissing: true);
            using var sourcePublication =
                sourceParent.OpenExistingChildForPublication(
                    Path.GetFileName(source));
            using var sourceAnchor =
                sourcePublication.OpenCreatedDirectoryAnchor();
            if (!sourceAnchor.IsOnSameVolume(destinationParent))
            {
                return null;
            }
            using var occupied =
                destinationParent.TryOpenExistingChildForPublication(
                    Path.GetFileName(destination));
            if (occupied != null || File.Exists(destination))
            {
                return false;
            }

            // Prove directory barriers are available before changing either
            // public pathname.
            FlushFileMoveDirectory(
                sourceParent,
                "same-volume directory source capability");
            FlushFileMoveDirectory(
                destinationParent,
                "same-volume directory destination capability");
            using var relocated = sourcePublication.MovePinnedDirectoryTo(
                destinationParent,
                Path.GetFileName(destination));
            FlushFileMoveDirectory(
                sourceParent,
                "same-volume directory source retirement");
            FlushFileMoveDirectory(
                destinationParent,
                "same-volume directory destination publication");
            return relocated.VisiblePathMatches();
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Pinned same-volume directory rename failed: {Source} -> {Destination}",
                LogRedaction.SanitizeFilePath(sourceDirectory),
                LogRedaction.SanitizeFilePath(destinationDirectory));
            return false;
        }
    }

    private static bool CanCopyDirectoryAcrossVolumesWithoutFidelityLoss(
        DirectoryCopySnapshot snapshot)
    {
        if (snapshot.Files
            .GroupBy(file => file.Identity)
            .Any(group => group.Count() > 1))
        {
            return false;
        }

        try
        {
            using var root =
                PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                    snapshot.SourceRoot);
            if (root.HasUnsupportedCrossVolumeMetadata())
            {
                return false;
            }
            foreach (var relativeDirectory in snapshot.RelativeDirectories)
            {
                using var directory =
                    PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                        ResolveSnapshotPath(
                            snapshot.SourceRoot,
                            relativeDirectory,
                            "cross-volume source directory"));
                if (directory.HasUnsupportedCrossVolumeMetadata())
                {
                    return false;
                }
            }
            foreach (var file in snapshot.Files)
            {
                var path = ResolveSnapshotPath(
                    snapshot.SourceRoot,
                    file.RelativePath,
                    "cross-volume source file");
                var parentPath = Path.GetDirectoryName(path)!;
                using var parent =
                    PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                        parentPath);
                using var entry = parent.OpenExistingFile(
                    Path.GetFileName(path),
                    requireDeleteAccess: false);
                if (entry.HasUnsupportedCrossVolumeMetadata())
                {
                    return false;
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or InvalidOperationException or PlatformNotSupportedException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
