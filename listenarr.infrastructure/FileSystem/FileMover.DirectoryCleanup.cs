using Microsoft.Extensions.Logging;

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
        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                sourceRoot,
                out var sourceFiles,
                out var sourceDirectories,
                out var reason))
        {
            return new DirectoryCopyCleanupResult(false, false, reason);
        }

        foreach (var sourceFile in sourceFiles)
        {
            if (!TryMapCopiedPath(
                    sourceRoot,
                    destinationRoot,
                    sourceFile,
                    out var destinationFile)
                || !File.Exists(destinationFile)
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
        foreach (var sourceFile in sourceFiles)
        {
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
                    destinationFile))
            {
                sourceChanged = true;
            }
        }

        foreach (var sourceDirectory in sourceDirectories
            .OrderByDescending(path => path.Length))
        {
            if (!FileSystemSafety.TryDeleteEmptyDirectory(
                    sourceDirectory,
                    [sourceRoot],
                    out _))
            {
                sourceChanged = true;
            }
        }

        if (!FileSystemSafety.TryDeleteEmptyDirectory(
                sourceRoot,
                [sourceRoot],
                out var rootReason))
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
                    : rootReason);
    }

    private async Task<bool> TryRemoveVerifiedCopiedFileAsync(
        string sourceRoot,
        string destinationRoot,
        string sourceFile,
        string destinationFile)
    {
        var quarantinePath = $"{sourceFile}{CopyCleanupMarker}{Guid.NewGuid():N}";
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
                || !FileSystemSafety.TryValidateMutationTarget(
                    quarantinePath,
                    [sourceRoot],
                    out quarantinePath,
                    out _)
                || !File.Exists(destinationFile)
                || (File.GetAttributes(sourceFile) & FileAttributes.ReparsePoint) != 0
                || (File.GetAttributes(destinationFile) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            File.Move(sourceFile, quarantinePath, overwrite: false);
            if ((File.GetAttributes(quarantinePath) & FileAttributes.ReparsePoint) != 0
                || !File.Exists(destinationFile)
                || (File.GetAttributes(destinationFile) & FileAttributes.ReparsePoint) != 0
                || !await FileSystemSafety.FilesHaveSameContentAsync(
                    quarantinePath,
                    destinationFile))
            {
                TryRestoreQuarantinedSourceFile(sourceFile, quarantinePath);
                return false;
            }

            File.Delete(quarantinePath);
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TryRestoreQuarantinedSourceFile(sourceFile, quarantinePath);
            _logger.LogDebug(
                exception,
                "Preserved source file after directory copy because verified cleanup did not complete: {Source}",
                LogRedaction.SanitizeFilePath(sourceFile));
            return false;
        }
    }

    private void TryRestoreQuarantinedSourceFile(
        string sourceFile,
        string quarantinePath)
    {
        if (!File.Exists(quarantinePath))
        {
            return;
        }

        try
        {
            if (!File.Exists(sourceFile) && !Directory.Exists(sourceFile))
            {
                File.Move(quarantinePath, sourceFile, overwrite: false);
                return;
            }

            _logger.LogWarning(
                "Preserved quarantined source file {Quarantine} because the original path was recreated at {Source}",
                LogRedaction.SanitizeFilePath(quarantinePath),
                LogRedaction.SanitizeFilePath(sourceFile));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                exception,
                "Unable to restore quarantined source file {Quarantine} to {Source}; both paths were preserved",
                LogRedaction.SanitizeFilePath(quarantinePath),
                LogRedaction.SanitizeFilePath(sourceFile));
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
