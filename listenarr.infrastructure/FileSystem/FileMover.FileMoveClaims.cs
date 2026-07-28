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
        SourceRecreated,
        Blocked
    }

    private sealed record FileMoveStatePaths(
        string SourceStateDirectory,
        string DestinationStateDirectory,
        string SourceClaim,
        string DestinationStage,
        string DestinationPrevious,
        string OperationState,
        string GenerationFence);

    private async Task<VerifiedFileMoveRemovalOutcome> TryRemoveVerifiedFileMoveSourceWithClaimsAsync(
        FileMoveGateLease lease,
        Guid? operationId)
    {
        var sourceFile = lease.SourcePath;
        var destinationFile = lease.DestinationPath;
        var sourceIdentity = lease.SourceIdentity;
        var destinationIdentity = lease.DestinationIdentity;
        if (!lease.SourceParent.VisiblePathMatches()
            || !lease.DestinationParent.VisiblePathMatches())
        {
            return VerifiedFileMoveRemovalOutcome.NotRemoved;
        }
        var state = GetFileMoveStatePaths(
            sourceFile,
            destinationFile,
            sourceIdentity,
            destinationIdentity);
        var sourceRetirementCommitted = false;
        PinnedDirectoryCreation? sourceStatePublication = null;
        PinnedDirectoryCreation? destinationStatePublication = null;

        try
        {
            using var initialSource = lease.SourceParent.TryOpenExistingFile(
                lease.SourceName,
                requireDeleteAccess: true);
            using var initialDestination = lease.DestinationParent.TryOpenExistingFile(
                lease.DestinationName,
                requireDeleteAccess: true);
            using var existingSourceState =
                lease.SourceParent.TryOpenExistingChildForPublication(
                    Path.GetFileName(state.SourceStateDirectory));
            using var existingDestinationState =
                lease.DestinationParent.TryOpenExistingChildForPublication(
                    Path.GetFileName(state.DestinationStateDirectory));
            if (initialSource == null
                || existingSourceState != null
                || existingDestinationState != null)
            {
                return VerifiedFileMoveRemovalOutcome.NotRemoved;
            }
            FlushFileMoveDirectory(
                lease.SourceParent,
                "source durability capability");
            FlushFileMoveDirectory(
                lease.DestinationParent,
                "destination durability capability");

            sourceStatePublication = CreateAnchoredFileMoveStateDirectory(
                lease.SourceParent,
                Path.GetFileName(state.SourceStateDirectory));
            using var sourceState = sourceStatePublication.OpenCreatedDirectoryAnchor();
            if (AfterSourceStateCreatedForTestAsync != null)
            {
                await AfterSourceStateCreatedForTestAsync();
            }

            var sourceSnapshot = await CaptureFileMoveContentAsync(initialSource);
            var useNativeRename = !DisableNativeFileRenameForTest
                && initialSource.IsOnSameVolume(
                    lease.DestinationParent);
            if (!useNativeRename
                && !DisableNativeFileRenameForTest
                && initialSource.HasUnsupportedCrossVolumeMetadata())
            {
                throw new PlatformNotSupportedException(
                    "Cross-volume move was blocked because hardlinks or extended metadata cannot be reproduced without fidelity loss.");
            }
            using var operationState = sourceState.CreateNewFile(
                "operation.state");
            await WriteFileMoveContentAsync(
                operationState,
                sourceSnapshot,
                operationId,
                sourceIdentity,
                destinationIdentity,
                useNativeRename);
            FlushFileMoveDirectory(sourceState, "source operation state");
            FlushFileMoveDirectory(lease.SourceParent, "source state publication");

            initialSource.MoveTo(sourceState, "source.claim");
            initialSource.Dispose();
            FlushFileMoveDirectory(lease.SourceParent, "source quarantine removal");
            FlushFileMoveDirectory(sourceState, "source quarantine publication");
            using var sourceClaim = sourceState.OpenExistingFile(
                "source.claim",
                requireDeleteAccess: true);
            if (AfterSourceQuarantinedForTestAsync != null)
            {
                await AfterSourceQuarantinedForTestAsync(
                    sourceFile,
                    state.SourceClaim);
            }

            if (!await FileMatchesMoveContentAsync(
                    sourceClaim,
                    sourceSnapshot))
            {
                throw new IOException(
                    "The quarantined source changed before destination staging.");
            }

            destinationStatePublication = CreateAnchoredFileMoveStateDirectory(
                lease.DestinationParent,
                Path.GetFileName(state.DestinationStateDirectory));
            using var destinationState =
                destinationStatePublication.OpenCreatedDirectoryAnchor();
            FlushFileMoveDirectory(
                lease.DestinationParent,
                "destination state publication");
            if (AfterDestinationStateCreatedForTestAsync != null)
            {
                await AfterDestinationStateCreatedForTestAsync();
            }

            if (initialDestination != null)
            {
                initialDestination.MoveTo(
                    destinationState,
                    "destination.previous");
                initialDestination.Dispose();
                FlushFileMoveDirectory(
                    lease.DestinationParent,
                    "previous destination quarantine removal");
                FlushFileMoveDirectory(
                    destinationState,
                    "previous destination quarantine publication");
            }
            PinnedDirectoryCreation.PinnedFileEntry? destinationStage = null;
            try
            {
                if (!useNativeRename)
                {
                    destinationStage = destinationState.CreateNewFile(
                        "destination.stage");
                    await using (var sourceStream = sourceClaim.OpenReadStream(
                        bufferSize: 128 * 1024,
                        asynchronous: false))
                    await using (var destinationStream = destinationStage.OpenWriteStream(
                        bufferSize: 128 * 1024,
                        asynchronous: false))
                    {
                        sourceStream.Position = 0;
                        await sourceStream.CopyToAsync(destinationStream);
                        await destinationStream.FlushAsync();
                        destinationStream.Flush(flushToDisk: true);
                    }
                    sourceClaim.PreserveMetadataTo(destinationStage);
                    destinationStage.FlushToDisk();
                    FlushFileMoveDirectory(
                        destinationState,
                        "destination stage bytes and metadata");
                }

                if (AfterDestinationQuarantinedForTestAsync != null)
                {
                    await AfterDestinationQuarantinedForTestAsync(
                        destinationFile,
                        initialDestination != null
                            ? state.DestinationPrevious
                            : state.DestinationStage);
                }
                var sourceStillMatches = await FileMatchesMoveContentAsync(
                    sourceClaim,
                    sourceSnapshot);
                var destinationMatches = useNativeRename
                    || await FileMatchesMoveContentAsync(
                        destinationStage!,
                        sourceSnapshot);
                if (!sourceStillMatches || !destinationMatches)
                {
                    throw new IOException(
                        "The verified source or destination stage changed before source retirement.");
                }

                using var generationFence = sourceState.CreateNewFile(
                    "replacement-generation.fence");
                generationFence.FlushToDisk();
                FlushFileMoveDirectory(sourceState, "source retirement fence");
                sourceRetirementCommitted = true;
                if (AfterSourceRetirementCommittedForTestAsync != null)
                {
                    await AfterSourceRetirementCommittedForTestAsync();
                }

                if (useNativeRename)
                {
                    if (lease.DestinationParent.TryOpenExistingFile(
                            lease.DestinationName,
                            requireDeleteAccess: false) is { } recreatedDestination)
                    {
                        recreatedDestination.Dispose();
                        return VerifiedFileMoveRemovalOutcome.PathRecreated;
                    }
                    sourceClaim.MoveTo(
                        lease.DestinationParent,
                        lease.DestinationName);
                }
                else
                {
                    sourceClaim.Delete(immediateWindows: true);
                    sourceClaim.Dispose();
                    FlushFileMoveDirectory(sourceState, "source claim retirement");
                    if (AfterSourceClaimDeletedForTestAsync != null)
                    {
                        await AfterSourceClaimDeletedForTestAsync();
                    }
                    if (lease.DestinationParent.TryOpenExistingFile(
                            lease.DestinationName,
                            requireDeleteAccess: false) is { } recreatedDestination)
                    {
                        recreatedDestination.Dispose();
                        return VerifiedFileMoveRemovalOutcome.PathRecreated;
                    }
                    destinationStage!.MoveTo(
                        lease.DestinationParent,
                        lease.DestinationName);
                }
                FlushFileMoveDirectory(
                    lease.DestinationParent,
                    "destination publication");
                if (!useNativeRename)
                {
                    FlushFileMoveDirectory(
                        destinationState,
                        "destination stage retirement");
                }
                if (AfterDestinationPublishedForTestAsync != null)
                {
                    await AfterDestinationPublishedForTestAsync(destinationFile);
                }

                using var previous = destinationState.TryOpenExistingFile(
                    "destination.previous",
                    requireDeleteAccess: true);
                previous?.Delete(immediateWindows: true);
                previous?.Dispose();
                FlushFileMoveDirectory(
                    destinationState,
                    "previous destination retirement");
                using var recreatedSource = lease.SourceParent.TryOpenExistingFile(
                    lease.SourceName,
                    requireDeleteAccess: false);
                var sourcePathWasRecreated = recreatedSource != null;
                if (!sourcePathWasRecreated)
                {
                    generationFence.Delete(immediateWindows: true);
                    generationFence.Dispose();
                    operationState.Delete(immediateWindows: true);
                    operationState.Dispose();
                    FlushFileMoveDirectory(sourceState, "operation state retirement");
                }
                sourceState.Dispose();
                destinationState.Dispose();
                if (!sourcePathWasRecreated)
                {
                    sourceStatePublication.DeletePinnedEmptyDirectory(
                        Path.GetFileName(state.SourceStateDirectory),
                        immediateWindows: true);
                }
                destinationStatePublication.DeletePinnedEmptyDirectory(
                    Path.GetFileName(state.DestinationStateDirectory),
                    immediateWindows: true);
                FlushFileMoveDirectory(
                    lease.DestinationParent,
                    "destination state retirement");
                if (!sourcePathWasRecreated)
                {
                    FlushFileMoveDirectory(
                        lease.SourceParent,
                        "source state retirement");
                }
                if (AfterFileMoveStateCleanedForTestAsync != null)
                {
                    await AfterFileMoveStateCleanedForTestAsync();
                }

                return sourcePathWasRecreated
                    ? VerifiedFileMoveRemovalOutcome.PathRecreated
                    : VerifiedFileMoveRemovalOutcome.Removed;
            }
            finally
            {
                destinationStage?.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not (
            OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            if (!sourceRetirementCommitted)
            {
                _ = await TryRecoverInterruptedFileMoveClaimsAsync(
                    lease,
                    operationId);
            }
            _logger.LogWarning(
                exception,
                sourceRetirementCommitted
                    ? "Preserved committed file-move state for recovery: {Source} -> {Destination}"
                    : "Preserved uncommitted file-move state after verified cleanup failed: {Source} -> {Destination}",
                LogRedaction.SanitizeFilePath(sourceFile),
                LogRedaction.SanitizeFilePath(destinationFile));
            return VerifiedFileMoveRemovalOutcome.NotRemoved;
        }
        finally
        {
            destinationStatePublication?.Dispose();
            sourceStatePublication?.Dispose();
        }
    }

}
