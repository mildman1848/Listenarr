namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private const string EmptySourceQuarantineDirectoryName =
        ".listenarr-empty-source";

    private async Task<bool> RecoverEmptySourceDirectoryQuarantineAsync(
        AudiobookContentMoveRequest request,
        ValidatedQuarantineOwnership ownership,
        string sourceParent,
        CancellationToken cancellationToken)
    {
        var quarantinePath = Path.Join(
            ownership.DirectoryPath,
            EmptySourceQuarantineDirectoryName);
        ValidateQuarantineMutationPath(ownership, quarantinePath);
        if (File.Exists(quarantinePath))
        {
            throw new MoveNeedsAttentionException(
                "The empty-source quarantine path is occupied by a file.");
        }

        var sourceExists = Directory.Exists(request.Source);
        if (!Directory.Exists(quarantinePath))
        {
            return sourceExists;
        }

        if (sourceExists)
        {
            throw new MoveNeedsAttentionException(
                "Both the source directory and its interrupted cleanup quarantine exist; both were preserved.");
        }

        if ((File.GetAttributes(quarantinePath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                "The interrupted source-directory quarantine is a symbolic link or reparse point.");
        }

        if (Directory.EnumerateFileSystemEntries(quarantinePath).Any())
        {
            if (!FileSystemSafety.TryValidateMutationTarget(
                    request.Source,
                    [sourceParent],
                    out var restoredSource,
                    out var reason))
            {
                throw new MoveNeedsAttentionException(reason);
            }

            await EnsureMutationAuthorizedAsync(
                request,
                request.Source,
                request.Target,
                cancellationToken);
            ValidateQuarantineMutationPath(ownership, quarantinePath);
            if (File.Exists(restoredSource) || Directory.Exists(restoredSource))
            {
                throw new MoveNeedsAttentionException(
                    "The source path was recreated while interrupted source cleanup was being restored; both paths were preserved.");
            }

            Directory.Move(quarantinePath, restoredSource);
            return true;
        }

        await EnsureMutationAuthorizedAsync(
            request,
            request.Source,
            request.Target,
            cancellationToken);
        ValidateQuarantineMutationPath(ownership, quarantinePath);
        if (Directory.EnumerateFileSystemEntries(quarantinePath).Any())
        {
            throw new MoveNeedsAttentionException(
                "The interrupted source-directory quarantine gained content and was preserved.");
        }

        Directory.Delete(quarantinePath, recursive: false);
        return false;
    }

    private async Task QuarantineAndDeleteEmptySourceDirectoryAsync(
        AudiobookContentMoveRequest request,
        ValidatedQuarantineOwnership ownership,
        string sourceParent,
        CancellationToken cancellationToken)
    {
        var quarantinePath = Path.Join(
            ownership.DirectoryPath,
            EmptySourceQuarantineDirectoryName);
        ValidateQuarantineMutationPath(ownership, quarantinePath);
        if (File.Exists(quarantinePath) || Directory.Exists(quarantinePath))
        {
            throw new MoveNeedsAttentionException(
                "The empty-source quarantine path is already occupied.");
        }

        if (!FileSystemSafety.TryValidateMutationTarget(
                request.Source,
                [sourceParent],
                out var safeSource,
                out var reason))
        {
            throw new MoveNeedsAttentionException(reason);
        }

        if (!Directory.Exists(safeSource)
            || (File.GetAttributes(safeSource) & FileAttributes.ReparsePoint) != 0
            || Directory.EnumerateFileSystemEntries(safeSource).Any())
        {
            throw new MoveNeedsAttentionException(
                "The empty source directory changed before quarantine.");
        }

        await EnsureMutationAuthorizedAsync(
            request,
            request.Source,
            request.Target,
            cancellationToken);
        ValidateQuarantineMutationPath(ownership, quarantinePath);
        if (!Directory.Exists(safeSource)
            || (File.GetAttributes(safeSource) & FileAttributes.ReparsePoint) != 0
            || Directory.EnumerateFileSystemEntries(safeSource).Any())
        {
            throw new MoveNeedsAttentionException(
                "The empty source directory changed during final authorization.");
        }

        Directory.Move(safeSource, quarantinePath);
        faultInjector?.OnSourceCleanupMutation(
            request.JobId,
            SourceCleanupFaultPoint.AfterEmptySourceDirectoryQuarantine);
        ValidateQuarantineMutationPath(ownership, quarantinePath);
        if (Directory.EnumerateFileSystemEntries(quarantinePath).Any())
        {
            TryRestoreEmptySourceDirectory(safeSource, quarantinePath);
            throw new MoveNeedsAttentionException(
                "The source directory gained content during quarantine and was preserved.");
        }

        await EnsureMutationAuthorizedAsync(
            request,
            request.Source,
            request.Target,
            cancellationToken);
        ValidateQuarantineMutationPath(ownership, quarantinePath);
        if (Directory.EnumerateFileSystemEntries(quarantinePath).Any())
        {
            TryRestoreEmptySourceDirectory(safeSource, quarantinePath);
            throw new MoveNeedsAttentionException(
                "The source-directory quarantine gained content before deletion and was preserved.");
        }

        Directory.Delete(quarantinePath, recursive: false);
    }

    private static void TryRestoreEmptySourceDirectory(
        string source,
        string quarantinePath)
    {
        if (!Directory.Exists(quarantinePath)
            || File.Exists(source)
            || Directory.Exists(source))
        {
            return;
        }

        Directory.Move(quarantinePath, source);
    }
}
