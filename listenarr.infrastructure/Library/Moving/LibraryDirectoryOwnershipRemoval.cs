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

        if (originalExists)
        {
            if (!LibraryDirectoryOwnershipMarker.ContainsOnlyInsideMarker(
                    ownership,
                    originalPath))
            {
                return LibraryDirectoryRemovalOutcome.Retained;
            }

            if (File.Exists(quarantinePath) || Directory.Exists(quarantinePath))
            {
                throw new InvalidOperationException(
                    "The owned directory removal quarantine path is already occupied.");
            }

            Directory.Move(originalPath, quarantinePath);
            try
            {
                LibraryDirectoryOwnershipMarker.Validate(
                    ownership,
                    quarantinePath);
            }
            catch (Exception exception) when (exception is
                ArgumentException or IOException or UnauthorizedAccessException
                    or InvalidOperationException or NotSupportedException)
            {
                TryRestoreQuarantine(originalPath, quarantinePath);
                throw;
            }
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

        LibraryDirectoryOwnershipMarker.Validate(ownership, quarantinePath);
        var insideMarkerPath = Path.Join(
            quarantinePath,
            LibraryDirectoryOwnershipMarker.FileName);
        if (Directory.EnumerateFileSystemEntries(quarantinePath)
            .Any(path => !string.Equals(path, insideMarkerPath, StringComparison.Ordinal)))
        {
            TryRestoreQuarantine(originalPath, quarantinePath);
            return LibraryDirectoryRemovalOutcome.Retained;
        }

        if (!FileSystemSafety.TryValidateMutationTarget(
                quarantinePath,
                [parentPath],
                out var validatedQuarantinePath,
                out var validationReason)
            || !FileSystemPathIdentity.AreEquivalent(
                validatedQuarantinePath,
                Path.GetFullPath(quarantinePath),
                ownership.GetIdentity().Semantics))
        {
            throw new InvalidOperationException(
                $"The owned directory removal quarantine is unsafe: {validationReason}");
        }

        cancellationToken.ThrowIfCancellationRequested();
        LibraryDirectoryOwnershipMarker.DeleteInsideMarker(
            ownership,
            quarantinePath);
        if (Directory.EnumerateFileSystemEntries(quarantinePath).Any())
        {
            throw new InvalidOperationException(
                "The owned directory removal quarantine changed after its inside marker was removed.");
        }

        if (!FileSystemSafety.TryDeleteEmptyDirectory(
                quarantinePath,
                [parentPath],
                out var deleteReason))
        {
            throw new InvalidOperationException(
                $"The owned directory removal quarantine could not be deleted safely: {deleteReason}");
        }

        return LibraryDirectoryRemovalOutcome.Removed;
    }

    private static void TryRestoreQuarantine(
        string originalPath,
        string quarantinePath)
    {
        if (!Directory.Exists(quarantinePath))
        {
            return;
        }
        if (File.Exists(originalPath) || Directory.Exists(originalPath))
        {
            throw new InvalidOperationException(
                "The original owned directory path was recreated while its quarantine was active.");
        }

        Directory.Move(quarantinePath, originalPath);
    }
}
