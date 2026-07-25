using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static async Task RemoveExistingPartialAsync(
        string partialFile,
        string destinationRoot,
        MoveJobEntry manifestEntry,
        bool destinationIsJobOwnedTemp,
        bool destinationHasStructuredOwnership,
        Func<Task> authorizeMutation,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(partialFile))
        {
            return;
        }

        if (!destinationHasStructuredOwnership)
        {
            throw new MoveNeedsAttentionException(
                "A job-shaped partial file exists without structured move ownership.");
        }

        ValidateExistingOwnedFile(partialFile, destinationRoot);
        if (!destinationIsJobOwnedTemp
            && !await FileMatchesManifestAsync(partialFile, manifestEntry, cancellationToken))
        {
            throw new MoveNeedsAttentionException(
                $"A direct-copy partial file does not match the persisted manifest and was preserved: {Path.GetFileName(partialFile)}");
        }

        await authorizeMutation();
        ValidateExistingOwnedFile(partialFile, destinationRoot);
        if (!destinationIsJobOwnedTemp
            && !await FileMatchesManifestAsync(partialFile, manifestEntry, cancellationToken))
        {
            throw new MoveNeedsAttentionException(
                $"A direct-copy partial file changed before cleanup and was preserved: {Path.GetFileName(partialFile)}");
        }
        DeleteValidatedOwnedFile(partialFile, destinationRoot);
    }

    private static void ValidateCopyMutationPath(
        string path,
        string destinationRoot)
    {
        ValidateExistingMoveDirectory(destinationRoot, "copy destination root");
        if (!FileSystemSafety.TryValidateMutationTarget(
                path,
                [destinationRoot],
                out path,
                out var reason))
        {
            throw new MoveNeedsAttentionException(reason);
        }

        var parent = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
        {
            ValidateExistingMoveDirectory(parent, "copy destination directory");
        }

        if (Directory.Exists(path))
        {
            ValidateExistingMoveDirectory(path, "copy destination directory");
        }
        else if (File.Exists(path)
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                "A move destination file is a symbolic link or reparse point.");
        }
    }

    private static void ValidateExistingOwnedFile(
        string path,
        string destinationRoot)
    {
        ValidateCopyMutationPath(path, destinationRoot);
        if (!File.Exists(path)
            || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                "An owned move file is missing or is a symbolic link or reparse point.");
        }
    }

    private static void DeleteValidatedOwnedFile(
        string path,
        string destinationRoot)
    {
        ValidateExistingOwnedFile(path, destinationRoot);
        if (!FileSystemSafety.TryDeleteFile(
                path,
                [destinationRoot],
                out var reason))
        {
            throw new MoveNeedsAttentionException(
                string.IsNullOrWhiteSpace(reason)
                    ? "An owned move file changed before pinned deletion."
                    : reason);
        }
    }

    private static async Task ValidateSourceCopyPathAsync(
        string sourceRoot,
        string sourceFile,
        MoveJobEntry manifestEntry,
        FileSystemPathSemantics sourceSemantics,
        CancellationToken cancellationToken)
    {
        ValidateMoveSourceRoot(sourceRoot);
        var reason = string.Empty;
        if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                sourceRoot,
                sourceFile,
                sourceSemantics,
                out _)
            || !FileSystemSafety.TryValidateMutationTarget(
                sourceFile,
                [sourceRoot],
                out sourceFile,
                out reason))
        {
            throw new MoveNeedsAttentionException(
                string.IsNullOrWhiteSpace(reason)
                    ? "The source copy path escaped the persisted source root."
                    : reason);
        }

        if (!File.Exists(sourceFile)
            || (File.GetAttributes(sourceFile) & FileAttributes.ReparsePoint) != 0
            || !await FileMatchesManifestAsync(
                sourceFile,
                manifestEntry,
                cancellationToken))
        {
            throw new MoveNeedsAttentionException(
                $"Source file changed or became linked before copying: {manifestEntry.RelativePath}");
        }
    }

    private static async Task<bool> FileMatchesManifestAsync(
        string path,
        MoveJobEntry manifestEntry,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path)
            || manifestEntry.EntryType != MoveJobEntryType.File
            || new FileInfo(path).Length != manifestEntry.Length
            || string.IsNullOrWhiteSpace(manifestEntry.Sha256))
        {
            return false;
        }

        return string.Equals(
            await ComputeSha256Async(path, cancellationToken),
            manifestEntry.Sha256,
            StringComparison.Ordinal);
    }

    private void PreserveFileMetadata(string sourceFile, string destinationFile)
    {
        try
        {
            var attributes = File.GetAttributes(sourceFile);
            File.SetAttributes(destinationFile, attributes);
            File.SetLastWriteTimeUtc(destinationFile, File.GetLastWriteTimeUtc(sourceFile));
            File.SetCreationTimeUtc(destinationFile, File.GetCreationTimeUtc(sourceFile));
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            logger.LogDebug(
                exception,
                "Non-fatal: failed to preserve attributes for {File}",
                LogRedaction.SanitizeFilePath(sourceFile));
        }
    }
}
