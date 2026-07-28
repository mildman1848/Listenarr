using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    public async Task<bool> PerformActionOn(
        FileAction action,
        string source,
        string? destination = null,
        Guid? operationId = null)
    {
        if (action == FileAction.None || destination == null) return true;
        if (await IsFilesystemAliasAsync(source, destination))
        {
            LogMutation(
                FileMutationOutcome.Blocked,
                action,
                source,
                destination,
                "Source and destination are linked aliases of the same file");
            return false;
        }
        if (await IsSameFilesystemPathAsync(source, destination))
        {
            LogMutation(
                FileMutationOutcome.Skipped,
                action,
                source,
                destination,
                "Source and destination identify the same filesystem path");
            return true;
        }

        try
        {
            switch (action)
            {
                case FileAction.Move:
                    return await MoveFileAsync(source, destination, operationId);
                case FileAction.HardlinkCopy:
                    return await HardlinkFileAsync(source, destination);
                case FileAction.Copy:
                    return await CopyFileAsync(source, destination);
            }

            return false;
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            LogMutation(FileMutationOutcome.Failed, action, source, destination, exception.Message);
            throw new InvalidOperationException($"Unable to perform {action} on {source} to {destination}", exception);
        }
    }

    private async Task<IdempotentFileMoveOutcome> TryCompleteIdempotentFileMoveAsync(
        FileMoveGateLease lease,
        Guid? operationId)
    {
        var sourceFile = lease.SourcePath;
        var destFile = lease.DestinationPath;
        var equivalence = await TryDetermineFilesystemPathEquivalenceAsync(
            sourceFile,
            destFile);
        if (equivalence == true)
        {
            return IdempotentFileMoveOutcome.Completed;
        }

        bool endpointsExist;
        using (var sourceEntry = lease.SourceParent.TryOpenExistingFile(
            lease.SourceName,
            requireDeleteAccess: false))
        using (var destinationEntry = lease.DestinationParent.TryOpenExistingFile(
            lease.DestinationName,
            requireDeleteAccess: false))
        {
            endpointsExist = sourceEntry != null && destinationEntry != null;
        }

        if (equivalence == null || !endpointsExist)
        {
            return IdempotentFileMoveOutcome.NotApplicable;
        }

        // Release the observation handles before opening the same entries with
        // delete access. Keeping them alive can self-block the pinned claim on
        // Windows and bypass the serialized move protocol.
        var removalOutcome = await TryRemoveVerifiedFileMoveSourceWithClaimsAsync(
            lease,
            operationId);
        if (removalOutcome == VerifiedFileMoveRemovalOutcome.NotRemoved)
        {
            return IdempotentFileMoveOutcome.SourcePathRecreated;
        }

        if (removalOutcome == VerifiedFileMoveRemovalOutcome.PathRecreated)
        {
            return IdempotentFileMoveOutcome.SourcePathRecreated;
        }

        LogMutation(
            FileMutationOutcome.Skipped,
            FileAction.Move,
            sourceFile,
            destFile,
            "Destination already has identical content; source removed");
        return IdempotentFileMoveOutcome.Completed;
    }

    private async Task<SameContentShortcutOutcome> TrySkipSameContentAsync(
        FileAction action,
        string sourceFile,
        string destFile)
    {
        if (!File.Exists(sourceFile) || !File.Exists(destFile))
        {
            return SameContentShortcutOutcome.NotApplicable;
        }

        if (await IsFilesystemAliasAsync(sourceFile, destFile))
        {
            LogMutation(
                FileMutationOutcome.Blocked,
                action,
                sourceFile,
                destFile,
                "Source and destination became linked aliases before the same-content shortcut");
            return SameContentShortcutOutcome.Blocked;
        }

        if (!await FileSystemSafety.FilesHaveSameContentAsync(sourceFile, destFile))
        {
            return SameContentShortcutOutcome.NotApplicable;
        }

        if (await IsFilesystemAliasAsync(sourceFile, destFile))
        {
            LogMutation(
                FileMutationOutcome.Blocked,
                action,
                sourceFile,
                destFile,
                "Source and destination became linked aliases before the same-content shortcut");
            return SameContentShortcutOutcome.Blocked;
        }

        if (!await FileSystemSafety.FilesHaveSameContentAsync(sourceFile, destFile))
        {
            return SameContentShortcutOutcome.NotApplicable;
        }

        LogMutation(
            FileMutationOutcome.Skipped,
            action,
            sourceFile,
            destFile,
            "Destination already has identical content");
        return SameContentShortcutOutcome.Completed;
    }

    private void LogMutation(FileMutationOutcome outcome, FileAction action, string source, string? destination, string? reason = null)
    {
        var result = new FileMutationResult(outcome, action, source, destination, reason);
        _logger.LogInformation(
            "File mutation {Outcome}: {Action} {Source} -> {Destination}. Reason: {Reason}",
            result.Outcome,
            result.Action,
            LogRedaction.SanitizeFilePath(result.SourcePath),
            LogRedaction.SanitizeFilePath(result.DestinationPath ?? string.Empty),
            LogRedaction.SanitizeText(result.Reason ?? string.Empty));
    }
}
