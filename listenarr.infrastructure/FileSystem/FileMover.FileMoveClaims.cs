/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private enum VerifiedFileMoveRemovalOutcome
    {
        Removed,
        NotRemoved,
        PathRecreated
    }

    private enum FileMoveClaimRecoveryOutcome
    {
        Ready,
        Completed,
        Blocked
    }

    private sealed record FileMoveStatePaths(
        string SourceStateDirectory,
        string DestinationStateDirectory,
        string SourceClaim,
        string DestinationStage,
        string GenerationFence);

    private async Task<VerifiedFileMoveRemovalOutcome> TryRemoveVerifiedFileMoveSourceWithClaimsAsync(
        string sourceFile,
        string destinationFile,
        string sourceIdentity,
        string destinationIdentity)
    {
        var state = GetFileMoveStatePaths(
            sourceFile,
            destinationFile,
            sourceIdentity,
            destinationIdentity);
        var sourceClaimOwned = false;
        var sourceRetirementCommitted = false;
        var sourceRetired = false;

        try
        {
            if (!File.Exists(sourceFile)
                || !File.Exists(destinationFile)
                || IsLinkedOrUnverifiableEntry(sourceFile)
                || IsLinkedOrUnverifiableEntry(destinationFile)
                || Directory.Exists(state.SourceStateDirectory)
                || Directory.Exists(state.DestinationStateDirectory))
            {
                return VerifiedFileMoveRemovalOutcome.NotRemoved;
            }

            CreatePrivateStateDirectory(state.SourceStateDirectory);
            if (AfterSourceStateCreatedForTestAsync != null)
            {
                await AfterSourceStateCreatedForTestAsync();
            }

            if (!FileSystemSafety.TryValidateMutationTarget(
                    state.SourceClaim,
                    [state.SourceStateDirectory],
                    out var sourceClaim,
                    out _))
            {
                TryDeleteEmptyStateDirectories(state);
                return VerifiedFileMoveRemovalOutcome.NotRemoved;
            }

            // Private state is a cooperative boundary. A process under this
            // account can already mutate every media path and is out of scope.
            File.Move(sourceFile, sourceClaim, overwrite: false);
            sourceClaimOwned = true;
            if (AfterSourceQuarantinedForTestAsync != null)
            {
                await AfterSourceQuarantinedForTestAsync(sourceFile, sourceClaim);
            }

            if (!File.Exists(destinationFile)
                || IsLinkedOrUnverifiableEntry(destinationFile)
                || !await FileSystemSafety.FilesHaveSameContentAsync(
                    sourceClaim,
                    destinationFile))
            {
                RestoreUncommittedFileMove(sourceFile, state);
                return FileMoveStateHasConflicts(sourceFile, destinationFile, state)
                    ? VerifiedFileMoveRemovalOutcome.PathRecreated
                    : VerifiedFileMoveRemovalOutcome.NotRemoved;
            }

            CreatePrivateStateDirectory(state.DestinationStateDirectory);
            if (AfterDestinationStateCreatedForTestAsync != null)
            {
                await AfterDestinationStateCreatedForTestAsync();
            }

            if (!FileSystemSafety.TryValidateMutationTarget(
                    state.DestinationStage,
                    [state.DestinationStateDirectory],
                    out var destinationStage,
                    out _))
            {
                RestoreUncommittedFileMove(sourceFile, state);
                return VerifiedFileMoveRemovalOutcome.NotRemoved;
            }

            // Keep the canonical destination available during verification.
            File.Copy(destinationFile, destinationStage, overwrite: false);
            if (AfterDestinationQuarantinedForTestAsync != null)
            {
                await AfterDestinationQuarantinedForTestAsync(
                    destinationFile,
                    destinationStage);
            }

            if (!await FileSystemSafety.FilesHaveSameContentAsync(
                    sourceClaim,
                    destinationStage))
            {
                RestoreUncommittedFileMove(sourceFile, state);
                return FileMoveStateHasConflicts(sourceFile, destinationFile, state)
                    ? VerifiedFileMoveRemovalOutcome.PathRecreated
                    : VerifiedFileMoveRemovalOutcome.NotRemoved;
            }

            // Persist the retirement decision before deleting the claimed source.
            // Recovery must treat every later entry at the original source path as
            // a replacement generation, including one created before publication.
            WriteGenerationFence(state.GenerationFence);
            sourceRetirementCommitted = true;
            if (AfterSourceRetirementCommittedForTestAsync != null)
            {
                await AfterSourceRetirementCommittedForTestAsync();
            }

            // Source-claim deletion commits while the verified stage survives.
            File.Delete(sourceClaim);
            sourceClaimOwned = false;
            sourceRetired = true;
            if (AfterSourceClaimDeletedForTestAsync != null)
            {
                await AfterSourceClaimDeletedForTestAsync();
            }

            File.Move(destinationStage, destinationFile, overwrite: true);
            if (AfterDestinationPublishedForTestAsync != null)
            {
                await AfterDestinationPublishedForTestAsync(destinationFile);
            }

            if (!PathEntryExists(sourceFile))
            {
                TryDeleteFile(state.GenerationFence);
            }

            TryDeleteEmptyStateDirectories(state);
            if (AfterFileMoveStateCleanedForTestAsync != null)
            {
                await AfterFileMoveStateCleanedForTestAsync();
            }

            // Preserve any new source-path generation after this commit.
            return VerifiedFileMoveRemovalOutcome.Removed;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            if (sourceClaimOwned && !sourceRetirementCommitted)
            {
                RestoreUncommittedFileMove(sourceFile, state);
            }

            _logger.LogWarning(
                exception,
                sourceRetirementCommitted || sourceRetired
                    ? "Preserved committed file-move state for recovery: {Source} -> {Destination}"
                    : "Preserved uncommitted file-move state after verified cleanup failed: {Source} -> {Destination}",
                LogRedaction.SanitizeFilePath(sourceFile),
                LogRedaction.SanitizeFilePath(destinationFile));
            return FileMoveStateHasConflicts(sourceFile, destinationFile, state)
                ? VerifiedFileMoveRemovalOutcome.PathRecreated
                : VerifiedFileMoveRemovalOutcome.NotRemoved;
        }
    }

    private async Task<FileMoveClaimRecoveryOutcome> TryRecoverInterruptedFileMoveClaimsAsync(
        string sourceFile,
        string destinationFile,
        string sourceIdentity,
        string destinationIdentity)
    {
        var state = GetFileMoveStatePaths(
            sourceFile,
            destinationFile,
            sourceIdentity,
            destinationIdentity);
        if (!string.Equals(sourceIdentity, destinationIdentity, StringComparison.Ordinal))
        {
            var reverseState = GetFileMoveStatePaths(
                destinationFile,
                sourceFile,
                destinationIdentity,
                sourceIdentity);
            if (FileMoveStateExists(reverseState))
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }
        }

        var sourceStateDirectoryExists = Directory.Exists(
            state.SourceStateDirectory);
        var destinationStateDirectoryExists = Directory.Exists(
            state.DestinationStateDirectory);
        if (!sourceStateDirectoryExists && !destinationStateDirectoryExists)
        {
            return FileMoveClaimRecoveryOutcome.Ready;
        }

        try
        {
            if (!TryValidateStateDirectory(state.SourceStateDirectory)
                || !TryValidateStateDirectory(state.DestinationStateDirectory)
                || !StateDirectoryContainsOnly(
                    state.SourceStateDirectory,
                    state.SourceClaim,
                    state.GenerationFence)
                || !StateDirectoryContainsOnly(
                    state.DestinationStateDirectory,
                    state.DestinationStage))
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }

            var sourceClaimExists = File.Exists(state.SourceClaim);
            var destinationStageExists = File.Exists(state.DestinationStage);
            var generationFenceExists = File.Exists(state.GenerationFence);
            if ((sourceClaimExists && IsLinkedOrUnverifiableEntry(state.SourceClaim))
                || (destinationStageExists
                    && IsLinkedOrUnverifiableEntry(state.DestinationStage))
                || (generationFenceExists
                    && IsLinkedOrUnverifiableEntry(state.GenerationFence)))
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }

            if (sourceClaimExists)
            {
                if (generationFenceExists)
                {
                    if (!destinationStageExists
                        || !await FileSystemSafety.FilesHaveSameContentAsync(
                            state.SourceClaim,
                            state.DestinationStage))
                    {
                        return FileMoveClaimRecoveryOutcome.Blocked;
                    }

                    File.Delete(state.SourceClaim);
                }
                else
                {
                    // The commit point was not crossed. Restore the source generation
                    // and discard only the redundant destination copy.
                    TryRestoreStateFile(state.SourceClaim, sourceFile);
                    if (File.Exists(state.SourceClaim))
                    {
                        return FileMoveClaimRecoveryOutcome.Blocked;
                    }

                    TryDeleteFile(state.DestinationStage);
                    TryDeleteEmptyStateDirectories(state);
                    return FileMoveClaimRecoveryOutcome.Ready;
                }
            }

            if (destinationStageExists)
            {
                File.Move(state.DestinationStage, destinationFile, overwrite: true);
            }

            if (generationFenceExists)
            {
                if (!File.Exists(destinationFile)
                    || IsLinkedOrUnverifiableEntry(destinationFile))
                {
                    return FileMoveClaimRecoveryOutcome.Blocked;
                }

                if (!PathEntryExists(sourceFile))
                {
                    File.Delete(state.GenerationFence);
                }

                TryDeleteEmptyStateDirectories(state);
                return FileMoveClaimRecoveryOutcome.Completed;
            }

            TryDeleteEmptyStateDirectories(state);

            // Without the durable retirement fence, a missing source is ambiguous.
            // An empty state directory or an existing destination cannot prove that
            // this move retired the source, so recovery must fail closed.
            return PathEntryExists(sourceFile)
                ? FileMoveClaimRecoveryOutcome.Ready
                : FileMoveClaimRecoveryOutcome.Blocked;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _logger.LogWarning(
                exception,
                "Blocked file move because interrupted state could not be recovered: {Source} -> {Destination}",
                LogRedaction.SanitizeFilePath(sourceFile),
                LogRedaction.SanitizeFilePath(destinationFile));
            return FileMoveClaimRecoveryOutcome.Blocked;
        }
    }
}
