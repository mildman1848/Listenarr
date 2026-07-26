using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private static bool FileMoveStateExists(FileMoveStatePaths state) =>
        PathEntryExists(state.SourceStateDirectory)
        || PathEntryExists(state.DestinationStateDirectory);

    private static FileMoveStatePaths GetFileMoveStatePaths(
        string sourceFile,
        string destinationFile,
        string sourceIdentity,
        string destinationIdentity)
    {
        var normalizedSource = Path.GetFullPath(sourceFile);
        var normalizedDestination = Path.GetFullPath(destinationFile);
        var token = HashPathIdentity($"{sourceIdentity}\0{destinationIdentity}");
        var sourceStateDirectory = Path.Join(
            Path.GetDirectoryName(normalizedSource)!,
            $".listenarr-file-source-{token}.state");
        var destinationStateDirectory = Path.Join(
            Path.GetDirectoryName(normalizedDestination)!,
            $".listenarr-file-destination-{token}.state");
        return new FileMoveStatePaths(
            sourceStateDirectory,
            destinationStateDirectory,
            Path.Join(sourceStateDirectory, "source.claim"),
            Path.Join(destinationStateDirectory, "destination.stage"),
            Path.Join(destinationStateDirectory, "destination.previous"),
            Path.Join(sourceStateDirectory, "replacement-generation.fence"));
    }

    private static string HashPathIdentity(string identity) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant()[..24];

    private static void CreatePrivateStateDirectory(string path)
    {
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(temporaryPath);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temporaryPath,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
            }

            if (!TryValidateStateDirectory(temporaryPath)
                || Directory.EnumerateFileSystemEntries(temporaryPath).Any())
            {
                throw new IOException("Temporary file-move state is unsafe.");
            }

            Directory.Move(temporaryPath, path);
        }
        finally
        {
            if (Directory.Exists(temporaryPath)
                && !Directory.EnumerateFileSystemEntries(temporaryPath).Any())
            {
                Directory.Delete(temporaryPath);
            }
        }
    }

    private static bool StateDirectoryContainsOnly(
        string directoryPath,
        params string[] allowedPaths)
    {
        if (!Directory.Exists(directoryPath))
        {
            return true;
        }

        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var allowed = allowedPaths
            .Select(Path.GetFullPath)
            .ToHashSet(comparer);
        return Directory.EnumerateFileSystemEntries(directoryPath)
            .Select(Path.GetFullPath)
            .All(allowed.Contains);
    }

    private static bool TryValidateStateDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return true;
        }

        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        var mode = File.GetUnixFileMode(path);
        var disallowed = UnixFileMode.GroupRead
            | UnixFileMode.GroupWrite
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherWrite
            | UnixFileMode.OtherExecute;
        return (mode & disallowed) == 0;
    }

    private void RestoreUncommittedFileMove(
        string sourceFile,
        string destinationFile,
        FileMoveStatePaths state)
    {
        TryRestoreStateFile(state.SourceClaim, sourceFile);
        TryDeleteFile(state.DestinationStage);
        TryRestoreStateFile(state.DestinationPrevious, destinationFile);
        TryDeleteEmptyStateDirectories(state);
    }

    private static void WriteGenerationFence(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        stream.Flush(flushToDisk: true);
    }

    private static void TryDeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private void TryRestoreStateFile(string statePath, string originalPath)
    {
        if (!File.Exists(statePath))
        {
            return;
        }

        try
        {
            if (!PathEntryExists(originalPath))
            {
                File.Move(statePath, originalPath, overwrite: false);
                return;
            }

            _logger.LogWarning(
                "Preserved file-move state {StatePath} because its original path was recreated at {Original}",
                LogRedaction.SanitizeFilePath(statePath),
                LogRedaction.SanitizeFilePath(originalPath));
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Unable to restore file-move state {StatePath} to {Original}; both paths were preserved",
                LogRedaction.SanitizeFilePath(statePath),
                LogRedaction.SanitizeFilePath(originalPath));
        }
    }

    private static bool FileMoveStateHasConflicts(
        string sourceFile,
        string destinationFile,
        FileMoveStatePaths state) =>
        (PathEntryExists(sourceFile) && File.Exists(state.SourceClaim))
        || (PathEntryExists(destinationFile) && File.Exists(state.DestinationStage))
        || (PathEntryExists(destinationFile) && File.Exists(state.DestinationPrevious));

    private static void TryDeleteEmptyStateDirectories(FileMoveStatePaths state)
    {
        TryDeleteEmptyStateDirectory(state.SourceStateDirectory);
        if (!string.Equals(
                state.SourceStateDirectory,
                state.DestinationStateDirectory,
                StringComparison.Ordinal))
        {
            TryDeleteEmptyStateDirectory(state.DestinationStateDirectory);
        }
    }

    private static void TryDeleteEmptyStateDirectory(string path)
    {
        if (Directory.Exists(path)
            && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }

    private static bool PathEntryExists(string path) =>
        File.Exists(path) || Directory.Exists(path);

    private static PinnedDirectoryCreation CreateAnchoredFileMoveStateDirectory(
        PinnedDirectoryCreation.PinnedDirectoryAnchor parent,
        string stateName)
    {
        var creation = parent.TryCreateChildForPublication(stateName);
        try
        {
            if (!creation.Created || !creation.VisiblePathMatches())
            {
                throw new IOException(
                    "The deterministic file-move state directory is already occupied.");
            }
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    creation.FullPath,
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute);
                if (!creation.VisiblePathMatches())
                {
                    throw new IOException(
                        "The file-move state directory changed while permissions were restricted.");
                }
            }

            return creation;
        }
        catch
        {
            creation.Dispose();
            throw;
        }
    }

    private static bool AnchoredStateContainsOnly(
        PinnedDirectoryCreation.PinnedDirectoryAnchor state,
        params string[] allowedNames)
    {
        if (!state.VisiblePathMatches())
        {
            return false;
        }
        var comparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var allowed = allowedNames.ToHashSet(comparer);
        var actual = Directory.EnumerateFileSystemEntries(state.FullPath)
            .Select(Path.GetFileName)
            .ToList();
        return state.VisiblePathMatches()
            && actual.All(name => name != null && allowed.Contains(name));
    }

    private static void TryDeleteAnchoredStateDirectory(
        PinnedDirectoryCreation? publication,
        string stateName)
    {
        if (publication == null)
        {
            return;
        }
        try
        {
            publication.DeletePinnedEmptyDirectory(
                stateName,
                immediateWindows: true);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception
                or InvalidOperationException)
        {
            // Preserve non-empty or changed recovery state.
        }
    }
}
