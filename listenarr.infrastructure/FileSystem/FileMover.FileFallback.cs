using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    internal const string FileMoveStagingMarkerName = ".listenarr-file-move-owner";

    private enum FileMoveFallbackOutcome
    {
        Success,
        CopyFailed,
        SourceRetained
    }

    private async Task<FileMoveFallbackOutcome> TryManagedFileMoveFallbackAsync(
        string sourceFile,
        string destinationFile,
        string sourceIdentity,
        string destinationIdentity)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationFile);
        if (string.IsNullOrWhiteSpace(destinationDirectory)
            || !Directory.Exists(destinationDirectory))
        {
            return FileMoveFallbackOutcome.CopyFailed;
        }

        var stagingPath = Path.Join(
            destinationDirectory,
            $".{Path.GetFileName(destinationFile)}.listenarr-move-{Guid.NewGuid():N}.partial");
        var stagingCreated = false;
        var destinationPublished = false;
        try
        {
            File.Copy(sourceFile, stagingPath, overwrite: false);
            stagingCreated = true;
            if (IsLinkedOrUnverifiableEntry(sourceFile)
                || IsLinkedOrUnverifiableEntry(stagingPath)
                || !await FileSystemSafety.FilesHaveSameContentAsync(
                    sourceFile,
                    stagingPath))
            {
                return FileMoveFallbackOutcome.CopyFailed;
            }

            File.Move(stagingPath, destinationFile, overwrite: true);
            stagingCreated = false;
            destinationPublished = true;
            return await TryRemoveVerifiedFileMoveSourceAsync(
                sourceFile,
                destinationFile,
                sourceIdentity,
                destinationIdentity);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Verified file move fallback failed for {Source} -> {Destination}",
                LogRedaction.SanitizeFilePath(sourceFile),
                LogRedaction.SanitizeFilePath(destinationFile));
            return destinationPublished
                ? FileMoveFallbackOutcome.SourceRetained
                : FileMoveFallbackOutcome.CopyFailed;
        }
        finally
        {
            if (stagingCreated)
            {
                TryDeleteFileMoveStagingPath(stagingPath);
            }
        }
    }

    private async Task<FileMoveFallbackOutcome> TryRobocopyFileMoveFallbackAsync(
        string sourceFile,
        string destinationFile,
        string sourceIdentity,
        string destinationIdentity)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            || !_options.EnableRobocopy
            || _processRunner == null)
        {
            return FileMoveFallbackOutcome.CopyFailed;
        }

        var sourceDirectory = Path.GetDirectoryName(sourceFile) ?? string.Empty;
        var destinationDirectory = Path.GetDirectoryName(destinationFile);
        if (string.IsNullOrWhiteSpace(sourceDirectory)
            || string.IsNullOrWhiteSpace(destinationDirectory)
            || !Directory.Exists(destinationDirectory))
        {
            return FileMoveFallbackOutcome.CopyFailed;
        }

        var stagingDirectory = Path.Join(
            destinationDirectory,
            $".listenarr-file-move-{Guid.NewGuid():N}");
        var stagingOwnershipToken = Guid.NewGuid().ToString("N");
        var stagingDirectoryCreated = false;
        var destinationPublished = false;
        try
        {
            if (!ExclusiveDirectoryCreator.TryCreate(stagingDirectory))
            {
                return FileMoveFallbackOutcome.CopyFailed;
            }

            stagingDirectoryCreated = true;
            WriteFileMoveStagingMarker(
                stagingDirectory,
                stagingOwnershipToken);
            if (!TryValidateOwnedFileMoveStagingDirectory(
                    stagingDirectory,
                    destinationDirectory,
                    stagingOwnershipToken,
                    out var validatedStagingDirectory))
            {
                return FileMoveFallbackOutcome.CopyFailed;
            }

            var startInfo = CreateRobocopyStartInfo(
                sourceDirectory,
                validatedStagingDirectory,
                Path.GetFileName(sourceFile),
                "/COPY:DAT",
                "/R:0",
                "/W:0",
                "/NFL",
                "/NDL",
                "/NJH",
                "/NJS",
                "/NP");
            var processResult = await _processRunner.RunAsync(
                startInfo,
                _options.RobocopyTimeoutMs);
            if (!TryValidateOwnedFileMoveStagingDirectory(
                    stagingDirectory,
                    destinationDirectory,
                    stagingOwnershipToken,
                    out validatedStagingDirectory))
            {
                _logger.LogWarning(
                    "Robocopy file fallback staging ownership changed before publication.");
                return FileMoveFallbackOutcome.CopyFailed;
            }

            var stagedFile = Path.Join(
                validatedStagingDirectory,
                Path.GetFileName(sourceFile));
            if (processResult.TimedOut
                || processResult.ExitCode is < 0 or > 7
                || !File.Exists(stagedFile)
                || IsLinkedOrUnverifiableEntry(sourceFile)
                || IsLinkedOrUnverifiableEntry(stagedFile)
                || !await FileSystemSafety.FilesHaveSameContentAsync(
                    sourceFile,
                    stagedFile))
            {
                _logger.LogWarning(
                    "Robocopy file fallback did not produce a verified staged copy. Exit code: {ExitCode}",
                    processResult.ExitCode);
                return FileMoveFallbackOutcome.CopyFailed;
            }

            File.Move(stagedFile, destinationFile, overwrite: true);
            destinationPublished = true;
            return await TryRemoveVerifiedFileMoveSourceAsync(
                sourceFile,
                destinationFile,
                sourceIdentity,
                destinationIdentity);
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Robocopy file fallback failed for {Source} -> {Destination}",
                LogRedaction.SanitizeFilePath(sourceFile),
                LogRedaction.SanitizeFilePath(destinationFile));
            return destinationPublished
                ? FileMoveFallbackOutcome.SourceRetained
                : FileMoveFallbackOutcome.CopyFailed;
        }
        finally
        {
            if (stagingDirectoryCreated
                && TryValidateOwnedFileMoveStagingDirectory(
                    stagingDirectory,
                    destinationDirectory,
                    stagingOwnershipToken,
                    out var validatedStagingDirectory))
            {
                TryDeleteFileMoveStagingPath(Path.Join(
                    validatedStagingDirectory,
                    Path.GetFileName(sourceFile)));
                TryDeleteFileMoveStagingPath(Path.Join(
                    validatedStagingDirectory,
                    FileMoveStagingMarkerName));
                FileSystemSafety.TryDeleteEmptyDirectory(
                    validatedStagingDirectory,
                    [destinationDirectory],
                    out _);
            }
        }
    }

    private static void WriteFileMoveStagingMarker(
        string stagingDirectory,
        string ownershipToken)
    {
        var markerPath = Path.Join(
            stagingDirectory,
            FileMoveStagingMarkerName);
        using var stream = new FileStream(
            markerPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var writer = new StreamWriter(stream);
        writer.Write(ownershipToken);
    }

    internal static bool TryValidateOwnedFileMoveStagingDirectory(
        string stagingDirectory,
        string destinationDirectory,
        string ownershipToken,
        out string normalizedStagingDirectory)
    {
        normalizedStagingDirectory = string.Empty;
        try
        {
            if (!FileSystemSafety.TryValidateMutationTarget(
                    stagingDirectory,
                    [destinationDirectory],
                    out var validatedDirectory,
                    out _)
                || !Directory.Exists(validatedDirectory)
                || (File.GetAttributes(validatedDirectory) & FileAttributes.ReparsePoint) != 0
                || !string.Equals(
                    Path.GetFullPath(stagingDirectory),
                    validatedDirectory,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
            {
                return false;
            }

            var markerPath = Path.Join(
                validatedDirectory,
                FileMoveStagingMarkerName);
            if (!File.Exists(markerPath)
                || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var markerInfo = new FileInfo(markerPath);
            if (markerInfo.Length is <= 0 or > 128
                || !string.Equals(
                    File.ReadAllText(markerPath),
                    ownershipToken,
                    StringComparison.Ordinal))
            {
                return false;
            }

            normalizedStagingDirectory = validatedDirectory;
            return true;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            normalizedStagingDirectory = string.Empty;
            return false;
        }
    }

    private async Task<FileMoveFallbackOutcome> TryRemoveVerifiedFileMoveSourceAsync(
        string sourceFile,
        string destinationFile,
        string sourceIdentity,
        string destinationIdentity)
    {
        var removalOutcome = await TryRemoveVerifiedFileMoveSourceWithClaimsAsync(
            sourceFile,
            destinationFile,
            sourceIdentity,
            destinationIdentity);
        return removalOutcome == VerifiedFileMoveRemovalOutcome.Removed
            ? FileMoveFallbackOutcome.Success
            : FileMoveFallbackOutcome.SourceRetained;
    }

    private void TryDeleteFileMoveStagingPath(string stagingPath)
    {
        try
        {
            if (File.Exists(stagingPath))
            {
                File.Delete(stagingPath);
            }
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Failed to clean file move staging artifact {StagingPath}",
                LogRedaction.SanitizeFilePath(stagingPath));
        }
    }
}
