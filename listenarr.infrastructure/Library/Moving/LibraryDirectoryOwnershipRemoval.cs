using Listenarr.Domain.Common;

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
            var insideMarkerPath = Path.Join(
                quarantinePath,
                LibraryDirectoryOwnershipMarker.FileName);
            if (!File.Exists(insideMarkerPath)
                && !Directory.EnumerateFileSystemEntries(quarantinePath).Any())
            {
                var parentPath = Path.GetDirectoryName(ownership.CanonicalPath)
                    ?? throw new InvalidOperationException(
                        "The durable directory ownership path has no parent directory.");
                using var parent =
                    PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parentPath);
                using var publication =
                    parent.OpenExistingChildForPublication(
                        Path.GetFileName(quarantinePath));
                using var quarantine = publication.OpenCreatedDirectoryAnchor();
                EnsurePhysicalIdentity(ownership, quarantine);
                LibraryDirectoryOwnershipMarker.ValidateSiblingMarker(
                    ownership,
                    parent);
                if (Directory.EnumerateFileSystemEntries(quarantinePath).Any()
                    || !quarantine.VisiblePathMatches()
                    || !parent.VisiblePathMatches())
                {
                    throw new InvalidOperationException(
                        "The owned directory removal quarantine changed during recovery validation.");
                }

                return;
            }

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

    public static bool TryValidateLegacyMissingBothRecovery(
        LibraryDirectoryOwnership ownership,
        out LibraryDirectoryOwnershipMarker.MarkerPayload? legacyPayload)
    {
        ArgumentNullException.ThrowIfNull(ownership);
        legacyPayload = null;
        var originalPath = ownership.CanonicalPath;
        var quarantinePath = GetQuarantinePath(ownership);
        if (Directory.Exists(originalPath)
            || File.Exists(originalPath)
            || Directory.Exists(quarantinePath)
            || File.Exists(quarantinePath))
        {
            return false;
        }

        var parentPath = Path.GetDirectoryName(originalPath)
            ?? throw new InvalidOperationException(
                "The durable directory ownership path has no parent directory.");
        var siblingPath = LibraryDirectoryOwnershipMarker
            .GetMarkerPaths(ownership)[1];
        var temporaryName = Path.GetFileName(siblingPath) + ".v2.tmp";
        using var parent =
            PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parentPath);
        using var temporary = parent.TryOpenExistingFile(
            temporaryName,
            requireDeleteAccess: false);
        if (temporary != null || Directory.Exists(Path.Join(parentPath, temporaryName)))
        {
            throw new InvalidOperationException(
                "Legacy ownership removal proof is mixed with an incomplete v2 marker upgrade.");
        }

        using var sibling = parent.OpenExistingFile(
            Path.GetFileName(siblingPath),
            requireDeleteAccess: false);
        var payload = LibraryDirectoryOwnershipMarker.ReadPayload(sibling);
        if (LibraryDirectoryOwnershipMarker.MatchesCurrentPayload(
                ownership,
                payload))
        {
            return false;
        }
        if (!LibraryDirectoryOwnershipMarker.MatchesLegacyPayload(
                ownership,
                payload))
        {
            throw new InvalidOperationException(
                "The missing owned directory has no exact legacy removal proof.");
        }
        if (!parent.VisiblePathMatches()
            || !sibling.VisiblePathMatches()
            || Directory.Exists(originalPath)
            || File.Exists(originalPath)
            || Directory.Exists(quarantinePath)
            || File.Exists(quarantinePath)
            || File.Exists(Path.Join(parentPath, temporaryName))
            || Directory.Exists(Path.Join(parentPath, temporaryName))
            || !LibraryDirectoryOwnershipMarker.MatchesLegacyPayload(
                ownership,
                LibraryDirectoryOwnershipMarker.ReadPayload(sibling)))
        {
            throw new InvalidOperationException(
                "The legacy ownership removal proof changed during validation.");
        }

        legacyPayload = payload;
        return true;
    }

    public static LibraryDirectoryRemovalOutcome RemoveEmptyDirectory(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor parentAnchor,
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
        if (!FileSystemPathIdentity.AreEquivalent(
                parentAnchor.FullPath,
                parentPath,
                ownership.GetIdentity().Semantics)
            || !parentAnchor.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The authorized ownership parent no longer matches the persisted path.");
        }
        if (originalExists)
        {
            pinnedDirectory = parentAnchor.OpenExistingChildForPublication(
                Path.GetFileName(originalPath));
            using var originalAnchor = pinnedDirectory.OpenCreatedDirectoryAnchor();
            EnsurePhysicalIdentity(ownership, originalAnchor);
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
            EnsurePhysicalIdentity(ownership, quarantineAnchor);
            var insideMarkerPath = Path.Join(
                quarantinePath,
                LibraryDirectoryOwnershipMarker.FileName);
            if (!File.Exists(insideMarkerPath))
            {
                LibraryDirectoryOwnershipMarker.ValidateSiblingMarker(
                    ownership,
                    parentAnchor);
                if (Directory.EnumerateFileSystemEntries(quarantinePath).Any()
                    || !quarantineAnchor.VisiblePathMatches()
                    || !parentAnchor.VisiblePathMatches())
                {
                    throw new InvalidOperationException(
                        "The owned directory removal quarantine is not empty after its inside marker was retired.");
                }

                cancellationToken.ThrowIfCancellationRequested();
                LibraryDirectoryOwnershipMarker.ValidateSiblingMarker(
                    ownership,
                    parentAnchor);
                pinnedDirectory.DeletePinnedEmptyDirectory(
                    Path.GetFileName(quarantinePath));
                return LibraryDirectoryRemovalOutcome.Removed;
            }

            LibraryDirectoryOwnershipMarker.Validate(
                ownership,
                quarantineAnchor,
                parentAnchor);
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

    private static void EnsurePhysicalIdentity(
        LibraryDirectoryOwnership ownership,
        PinnedDirectoryCreation.PinnedDirectoryAnchor directory)
    {
        if (ownership.DirectoryObjectIdentityVersion != 1
            || string.IsNullOrWhiteSpace(ownership.DirectoryObjectIdentity)
            || !string.Equals(
                directory.GetDirectoryObjectIdentity(),
                ownership.DirectoryObjectIdentity,
                StringComparison.Ordinal)
            || !directory.VisiblePathMatches())
        {
            throw new InvalidOperationException(
                "The owned directory no longer matches its enrolled physical identity.");
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
