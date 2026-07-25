namespace Listenarr.Infrastructure.Library.Moving;

internal enum LibraryDirectoryRemovalOutcome
{
    Removed,
    AlreadyRemoved,
    Retained
}

internal static class LibraryDirectoryOwnershipRemoval
{
    private const string QuarantinePrefix = ".listenarr-directory-removing-";

    public static string GetQuarantinePath(LibraryDirectoryOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        LibraryDirectoryOwnershipMarker.ValidateOwnershipToken(
            ownership.OwnershipToken);
        var parent = Path.GetDirectoryName(ownership.CanonicalPath)
            ?? throw new InvalidOperationException(
                "The durable directory ownership path has no parent directory.");
        return Path.Join(parent, $"{QuarantinePrefix}{ownership.OwnershipToken}");
    }

    public static void ValidateRecoverableState(LibraryDirectoryOwnership ownership)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        var originalExists = Directory.Exists(ownership.CanonicalPath);
        var originalIsFile = File.Exists(ownership.CanonicalPath);
        var quarantinePath = GetQuarantinePath(ownership);
        var quarantineExists = Directory.Exists(quarantinePath);
        var quarantineIsFile = File.Exists(quarantinePath);
        if (originalIsFile || quarantineIsFile)
        {
            throw new InvalidOperationException(
                "An owned directory recovery path is occupied by a file.");
        }
        if (originalExists && quarantineExists)
        {
            throw new InvalidOperationException(
                "Both the owned directory and its removal quarantine exist.");
        }

        if (originalExists)
        {
            LibraryDirectoryOwnershipMarker.Validate(
                ownership,
                ownership.CanonicalPath);
            return;
        }

        if (quarantineExists)
        {
            LibraryDirectoryOwnershipMarker.Validate(
                ownership,
                quarantinePath);
            return;
        }

        if (!LibraryDirectoryOwnershipMarker.HasValidSiblingMarker(ownership))
        {
            throw new InvalidOperationException(
                "The missing owned directory has no valid interrupted-removal proof.");
        }
    }

    public static LibraryDirectoryRemovalOutcome RemoveEmptyDirectory(
        LibraryDirectoryOwnership ownership,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        cancellationToken.ThrowIfCancellationRequested();
        var originalPath = ownership.CanonicalPath;
        var parentPath = Path.GetDirectoryName(originalPath)
            ?? throw new InvalidOperationException(
                "The durable directory ownership path has no parent directory.");
        var quarantinePath = GetQuarantinePath(ownership);
        var originalExists = Directory.Exists(originalPath);
        var originalIsFile = File.Exists(originalPath);
        var quarantineExists = Directory.Exists(quarantinePath);
        var quarantineIsFile = File.Exists(quarantinePath);
        if (originalIsFile || quarantineIsFile)
        {
            throw new InvalidOperationException(
                "An owned directory removal path is occupied by a file.");
        }
        if (originalExists && quarantineExists)
        {
            throw new InvalidOperationException(
                "Both the owned directory and its removal quarantine exist.");
        }

        PinnedDirectoryCreation? pinnedDirectory = null;
        PinnedDirectoryCreation.PinnedDirectoryAnchor? quarantineAnchor = null;
        using var parentAnchor = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
            parentPath);
        if (originalExists)
        {
            pinnedDirectory = parentAnchor.OpenExistingChildForPublication(
                Path.GetFileName(originalPath));
            using var originalAnchor = pinnedDirectory.OpenCreatedDirectoryAnchor();
            if (!LibraryDirectoryOwnershipMarker.ContainsOnlyInsideMarker(
                    ownership,
                    originalAnchor,
                    parentAnchor))
            {
                pinnedDirectory.Dispose();
                return LibraryDirectoryRemovalOutcome.Retained;
            }

            if (File.Exists(quarantinePath) || Directory.Exists(quarantinePath))
            {
                throw new InvalidOperationException(
                    "The owned directory removal quarantine path is already occupied.");
            }

            quarantineAnchor = pinnedDirectory.RepublishPinnedDirectory(
                Path.GetFileName(originalPath),
                Path.GetFileName(quarantinePath));
            quarantineExists = true;
        }

        if (!quarantineExists)
        {
            if (!LibraryDirectoryOwnershipMarker.HasValidSiblingMarker(ownership))
            {
                throw new InvalidOperationException(
                    "The removed owned directory has no valid sibling ownership proof.");
            }

            return LibraryDirectoryRemovalOutcome.AlreadyRemoved;
        }

        pinnedDirectory ??= parentAnchor.OpenExistingChildForPublication(
            Path.GetFileName(quarantinePath));
        quarantineAnchor ??= pinnedDirectory.OpenCreatedDirectoryAnchor();
        try
        {
            LibraryDirectoryOwnershipMarker.Validate(
                ownership,
                quarantineAnchor,
                parentAnchor);
            var insideMarkerPath = Path.Join(
                quarantinePath,
                LibraryDirectoryOwnershipMarker.FileName);
            if (Directory.EnumerateFileSystemEntries(quarantinePath)
                .Any(path => !string.Equals(path, insideMarkerPath, StringComparison.Ordinal))
                || !quarantineAnchor.VisiblePathMatches())
            {
                RestorePinnedQuarantine(
                    pinnedDirectory,
                    originalPath,
                    quarantinePath);
                return LibraryDirectoryRemovalOutcome.Retained;
            }

            cancellationToken.ThrowIfCancellationRequested();
            LibraryDirectoryOwnershipMarker.DeleteInsideMarker(
                ownership,
                quarantineAnchor,
                parentAnchor);
            if (Directory.EnumerateFileSystemEntries(quarantinePath).Any()
                || !quarantineAnchor.VisiblePathMatches())
            {
                throw new InvalidOperationException(
                    "The owned directory removal quarantine changed after its inside marker was removed.");
            }

            pinnedDirectory.DeletePinnedEmptyDirectory(
                Path.GetFileName(quarantinePath));
            return LibraryDirectoryRemovalOutcome.Removed;
        }
        finally
        {
            quarantineAnchor?.Dispose();
            pinnedDirectory?.Dispose();
        }
    }

    private static void RestorePinnedQuarantine(
        PinnedDirectoryCreation pinnedDirectory,
        string originalPath,
        string quarantinePath)
    {
        if (File.Exists(originalPath) || Directory.Exists(originalPath))
        {
            throw new InvalidOperationException(
                "The original owned directory path was recreated while its quarantine was active.");
        }

        using var restored = pinnedDirectory.RepublishPinnedDirectory(
            Path.GetFileName(quarantinePath),
            Path.GetFileName(originalPath));
    }
}
