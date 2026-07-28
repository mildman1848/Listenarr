using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Renaming;

public partial class RenameService
{
    private static AudiobookPathRollbackState CaptureAudiobookPathRollbackState(
        Audiobook audiobook) =>
        new(
            audiobook.BasePath,
            audiobook.FilePath,
            audiobook.FileSize,
            (audiobook.Files ?? [])
                .ToDictionary(file => file.Id, file => file.CapturePathState()));

    private static DirectoryRollbackState CaptureDirectoryRollbackState(
        Audiobook audiobook,
        string sourcePath,
        string targetPath) =>
        new(
            sourcePath,
            targetPath,
            CaptureAudiobookPathRollbackState(audiobook));

    private static void RestoreAudiobookPathState(
        Audiobook audiobook,
        AudiobookPathRollbackState rollbackState)
    {
        audiobook.BasePath = rollbackState.BasePath;
        audiobook.FilePath = rollbackState.LegacyFilePath;
        audiobook.FileSize = rollbackState.FileSize;
        foreach (var file in audiobook.Files ?? [])
        {
            if (rollbackState.FileStates.TryGetValue(file.Id, out var fileState))
            {
                file.RestorePathState(fileState);
            }
        }
    }

    private async Task<bool> RollBackDirectoryMoveAsync(
        Audiobook audiobook,
        DirectoryRollbackState rollbackState,
        IReadOnlyCollection<string> allowedRoots,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!_fileSystem.TryValidateMutationTarget(
                    rollbackState.TargetPath,
                    allowedRoots,
                    out var rollbackSource,
                    out _)
                || !_fileSystem.TryValidateMutationTarget(
                    rollbackState.SourcePath,
                    allowedRoots,
                    out var rollbackDestination,
                    out _))
            {
                return false;
            }

            if (_fileSystem.DirectoryExists(rollbackSource))
            {
                if (_fileSystem.DirectoryExists(rollbackDestination))
                {
                    return false;
                }

                var parent = Path.GetDirectoryName(rollbackDestination);
                if (!string.IsNullOrWhiteSpace(parent))
                {
                    await EnsureOwnedRenameHierarchyAsync(
                        parent,
                        allowedRoots,
                        semantics,
                        audiobook.Id,
                        Guid.NewGuid(),
                        cancellationToken);
                }

                if (!await _fileMover.MoveDirectoryAsync(
                        rollbackSource,
                        rollbackDestination))
                {
                    return false;
                }
            }
            else if (!_fileSystem.DirectoryExists(rollbackDestination))
            {
                return false;
            }

            RestoreAudiobookPathState(audiobook, rollbackState.AudiobookState);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            && exception is not OutOfMemoryException
            && exception is not StackOverflowException)
        {
            _logger.LogError(
                exception,
                "Failed to roll back folder organize operation for audiobook {AudiobookId}",
                audiobook.Id);
            return false;
        }
    }

    private async Task<bool> RollBackFileRenamesAsync(
        Audiobook audiobook,
        IReadOnlyList<FileRenameResultItem> completedItems,
        AudiobookPathRollbackState rollbackState,
        IReadOnlyCollection<string> allowedRoots,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        var rollbackSucceeded = true;
        foreach (var item in completedItems.Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.Success
                || string.IsNullOrWhiteSpace(item.PreviousPath)
                || string.IsNullOrWhiteSpace(item.NewPath))
            {
                continue;
            }

            try
            {
                if (!PathsEqual(item.PreviousPath, item.NewPath, semantics))
                {
                    if (!_fileSystem.FileExists(item.NewPath))
                    {
                        rollbackSucceeded = false;
                        item.Error = "Rollback failed because the moved file could not be found.";
                        continue;
                    }

                    if (!_fileSystem.TryValidateMutationTarget(
                            item.NewPath,
                            allowedRoots,
                            out var rollbackSource,
                            out _)
                        || !_fileSystem.TryValidateMutationTarget(
                            item.PreviousPath,
                            allowedRoots,
                            out var rollbackDestination,
                            out _))
                    {
                        rollbackSucceeded = false;
                        item.Error = "Rollback paths could not be resolved safely within the allowed library roots.";
                        continue;
                    }

                    var parent = Path.GetDirectoryName(rollbackDestination);
                    if (!string.IsNullOrWhiteSpace(parent))
                    {
                        await EnsureOwnedRenameHierarchyAsync(
                            parent,
                            allowedRoots,
                            semantics,
                            audiobook.Id,
                            Guid.NewGuid(),
                            cancellationToken);
                    }

                    var moved = await _fileMover.PerformActionOn(
                        FileAction.Move,
                        rollbackSource,
                        rollbackDestination,
                        FileMoveOperationIdentity.Create(
                            "audiobook-file-rename-rollback",
                            audiobook.Id,
                            item.FileId,
                            rollbackSource,
                            rollbackDestination));
                    if (!moved)
                    {
                        rollbackSucceeded = false;
                        item.Error = "Rollback file move failed.";
                        continue;
                    }
                }

                if (item.FileId == 0)
                {
                    audiobook.FilePath = rollbackState.LegacyFilePath;
                }
                else
                {
                    var file = audiobook.Files?.FirstOrDefault(candidate => candidate.Id == item.FileId);
                    if (file == null
                        || !rollbackState.FileStates.TryGetValue(item.FileId, out var fileState))
                    {
                        rollbackSucceeded = false;
                        item.Error = "Rollback could not restore the tracked audiobook file state.";
                        continue;
                    }

                    file.RestorePathState(fileState);
                }

                item.Success = false;
                item.RolledBack = true;
                item.Error = "The file move was rolled back because the organize operation did not complete.";
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                && exception is not OutOfMemoryException
                && exception is not StackOverflowException)
            {
                rollbackSucceeded = false;
                item.Error = $"Rollback failed: {exception.Message}";
                _logger.LogError(
                    exception,
                    "Failed to roll back organize operation for audiobook {AudiobookId}, file {FileId}",
                    audiobook.Id,
                    item.FileId);
            }
        }

        if (rollbackSucceeded)
        {
            RestoreAudiobookPathState(audiobook, rollbackState);
        }
        else
        {
            UpdateAudiobookPathSummary(audiobook, null, semantics);
            try
            {
                await _audiobookRepository.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException
                && exception is not OutOfMemoryException
                && exception is not StackOverflowException)
            {
                _logger.LogCritical(
                    exception,
                    "Failed to persist actual partial organize state for audiobook {AudiobookId}",
                    audiobook.Id);
            }
        }

        return rollbackSucceeded;
    }

    private sealed record AudiobookPathRollbackState(
        string? BasePath,
        string? LegacyFilePath,
        long? FileSize,
        IReadOnlyDictionary<int, AudiobookFilePathState> FileStates);

    private sealed record DirectoryRollbackState(
        string SourcePath,
        string TargetPath,
        AudiobookPathRollbackState AudiobookState);
}
