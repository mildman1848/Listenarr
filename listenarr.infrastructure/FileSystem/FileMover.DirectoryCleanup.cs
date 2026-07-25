using Microsoft.Extensions.Logging;
using System.Security.Cryptography;

namespace Listenarr.Infrastructure.FileSystem;

internal sealed record DirectoryCopyCleanupResult(
    bool DestinationVerified,
    bool SourceRemoved,
    string? Reason = null);

public partial class FileMover
{
    private const string CopyCleanupMarker = ".listenarr-copy-cleanup-";

    internal bool TryRecoverInterruptedCopiedSourceCleanup(
        string sourceRoot,
        out string reason)
    {
        reason = string.Empty;
        BeforeDirectoryTreePreflightForTest?.Invoke();
        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                sourceRoot,
                out var sourceFiles,
                out _,
                out reason))
        {
            return false;
        }

        if (sourceFiles.Any(IsCopyCleanupQuarantinePath))
        {
            reason =
                "The source contains a reserved directory-copy cleanup artifact that cannot be attributed safely. It was preserved for operator review.";
            return false;
        }

        return true;
    }

    internal async Task<DirectoryCopyCleanupResult> CleanupCopiedSourceTreeAsync(
        string sourceRoot,
        string destinationRoot,
        Action? afterDestinationVerified = null)
    {
        if (!TryCaptureDirectoryCopySnapshot(
                sourceRoot,
                out var snapshot,
                out var reason)
            || snapshot == null)
        {
            return new DirectoryCopyCleanupResult(false, false, reason);
        }

        return await CleanupCopiedSourceTreeAsync(
            snapshot,
            destinationRoot,
            afterDestinationVerified);
    }

    private async Task<DirectoryCopyCleanupResult> CleanupCopiedSourceTreeAsync(
        DirectoryCopySnapshot snapshot,
        string destinationRoot,
        Action? afterDestinationVerified = null)
    {
        var sourceRoot = snapshot.SourceRoot;
        foreach (var fileSnapshot in snapshot.Files)
        {
            var sourceFile = ResolveSnapshotPath(
                sourceRoot,
                fileSnapshot.RelativePath,
                "source cleanup file");
            if (!TryMapCopiedPath(
                    sourceRoot,
                    destinationRoot,
                    sourceFile,
                    out var destinationFile)
                || !File.Exists(destinationFile)
                || !TryGetRegularFileIdentity(sourceFile, out var currentIdentity)
                || currentIdentity != fileSnapshot.Identity
                || !await FileSystemSafety.FilesHaveSameContentAsync(
                    sourceFile,
                    destinationFile))
            {
                return new DirectoryCopyCleanupResult(
                    false,
                    false,
                    "The copied destination could not be verified against the current source tree.");
            }
        }

        afterDestinationVerified?.Invoke();

        var sourceChanged = false;
        foreach (var fileSnapshot in snapshot.Files)
        {
            var sourceFile = ResolveSnapshotPath(
                sourceRoot,
                fileSnapshot.RelativePath,
                "source cleanup file");
            if (!File.Exists(sourceFile))
            {
                continue;
            }

            if (!TryMapCopiedPath(
                    sourceRoot,
                    destinationRoot,
                    sourceFile,
                    out var destinationFile)
                || !await TryRemoveVerifiedCopiedFileAsync(
                    sourceRoot,
                    destinationRoot,
                    sourceFile,
                    destinationFile,
                    fileSnapshot.Identity))
            {
                sourceChanged = true;
            }
        }

        foreach (var sourceDirectory in snapshot.RelativeDirectories
            .Select(path => ResolveSnapshotPath(
                sourceRoot,
                path,
                "source cleanup directory"))
            .OrderByDescending(path => path.Length))
        {
            var relativeDirectory = GetVerifiedRelativePath(
                sourceRoot,
                sourceDirectory);
            if (!snapshot.DirectoryIdentities.TryGetValue(
                    relativeDirectory,
                    out var expectedIdentity)
                || !TryDeletePinnedEmptyDirectory(
                    sourceDirectory,
                    expectedIdentity))
            {
                sourceChanged = true;
            }
        }

        if (!TryDeletePinnedEmptyDirectory(
                sourceRoot,
                snapshot.SourceRootIdentity))
        {
            sourceChanged = true;
        }

        var sourceRemoved = !Directory.Exists(sourceRoot);
        return new DirectoryCopyCleanupResult(
            true,
            sourceRemoved,
            sourceRemoved
                ? null
                : sourceChanged
                    ? "The destination was copied, but new or changed source content was preserved."
                    : "The verified source root could not be removed.");
    }

    private async Task<bool> TryRemoveVerifiedCopiedFileAsync(
        string sourceRoot,
        string destinationRoot,
        string sourceFile,
        string destinationFile,
        RegularFileIdentity expectedIdentity)
    {
        try
        {
            if (!FileSystemSafety.TryValidateMutationTarget(
                    sourceFile,
                    [sourceRoot],
                    out sourceFile,
                    out _)
                || !FileSystemSafety.TryValidateMutationTarget(
                    destinationFile,
                    [destinationRoot],
                    out destinationFile,
                    out _)
                || !File.Exists(destinationFile)
                || (File.GetAttributes(sourceFile) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(destinationFile) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var sourceParentPath = Path.GetDirectoryName(sourceFile)
                ?? throw new InvalidOperationException(
                    "The copied source file parent is unavailable.");
            var destinationParentPath = Path.GetDirectoryName(destinationFile)
                ?? throw new InvalidOperationException(
                    "The copied destination file parent is unavailable.");
            using var sourceParent =
                PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(sourceParentPath);
            using var sourceEntry = sourceParent.OpenExistingFile(
                Path.GetFileName(sourceFile),
                requireDeleteAccess: true);
            using var sourceHandle = sourceEntry.DuplicateHandleForOperation();
            if (!TryGetRegularFileIdentity(sourceHandle, out var pinnedIdentity)
                || pinnedIdentity != expectedIdentity)
            {
                return false;
            }

            using var destinationParent =
                PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(destinationParentPath);
            using var destinationEntry = destinationParent.OpenExistingFile(
                Path.GetFileName(destinationFile),
                requireDeleteAccess: false);
            await using var sourceStream = sourceEntry.OpenReadStream(
                bufferSize: 128 * 1024,
                asynchronous: false);
            var length = sourceStream.Length;
            var hash = await SHA256.HashDataAsync(sourceStream);
            if (!await destinationEntry.MatchesAsync(
                    length,
                    Convert.ToHexString(hash),
                    CancellationToken.None)
                || !sourceParent.VisiblePathMatches()
                || !destinationParent.VisiblePathMatches()
                || !sourceEntry.VisiblePathMatches()
                || !destinationEntry.VisiblePathMatches())
            {
                return false;
            }

            sourceEntry.Delete();
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogDebug(
                exception,
                "Preserved source file after directory copy because verified cleanup did not complete: {Source}",
                LogRedaction.SanitizeFilePath(sourceFile));
            return false;
        }
    }

    private static bool IsCopyCleanupQuarantinePath(string path)
    {
        var markerIndex = path.LastIndexOf(
            CopyCleanupMarker,
            StringComparison.Ordinal);
        if (markerIndex <= 0)
        {
            return false;
        }

        var token = path[(markerIndex + CopyCleanupMarker.Length)..];
        return token.Length == 32
            && Guid.TryParseExact(token, "N", out _);
    }

    private static bool TryDeletePinnedEmptyDirectory(
        string directoryPath,
        RegularFileIdentity expectedIdentity)
    {
        try
        {
            var fullPath = Path.GetFullPath(directoryPath);
            var parentPath = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(parentPath))
            {
                return false;
            }

            var childName = Path.GetFileName(fullPath);
            using var directory = PinnedDirectoryCreation.OpenExistingForPublication(
                parentPath,
                childName);
            using var anchor = directory.OpenCreatedDirectoryAnchor();
            using var handle = anchor.DuplicateHandleForOperation();
            if (!TryGetRegularFileIdentity(handle, out var currentIdentity)
                || currentIdentity != expectedIdentity
                || Directory.EnumerateFileSystemEntries(fullPath).Any()
                || !directory.VisiblePathMatches()
                || !anchor.VisiblePathMatches())
            {
                return false;
            }

            directory.DeletePinnedEmptyDirectory(childName);
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static bool TryMapCopiedPath(
        string sourceRoot,
        string destinationRoot,
        string sourcePath,
        out string destinationPath)
    {
        destinationPath = string.Empty;
        if (!FileSystemSafety.TryValidateMutationTarget(
                sourcePath,
                [sourceRoot],
                out var normalizedSource,
                out _))
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(
            Path.GetFullPath(sourceRoot),
            normalizedSource);
        if (string.IsNullOrWhiteSpace(relativePath)
            || relativePath == "."
            || Path.IsPathRooted(relativePath)
            || relativePath.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => segment is "." or ".."))
        {
            return false;
        }

        return FileSystemSafety.TryValidateMutationTarget(
            Path.Join(destinationRoot, relativePath),
            [destinationRoot],
            out destinationPath,
            out _);
    }
}
