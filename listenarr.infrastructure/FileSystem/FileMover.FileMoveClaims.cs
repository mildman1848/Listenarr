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
        string DestinationPrevious,
        string GenerationFence);

    private async Task<VerifiedFileMoveRemovalOutcome> TryRemoveVerifiedFileMoveSourceWithClaimsAsync(
        FileMoveGateLease lease)
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

            sourceStatePublication = CreateAnchoredFileMoveStateDirectory(
                lease.SourceParent,
                Path.GetFileName(state.SourceStateDirectory));
            using var sourceState = sourceStatePublication.OpenCreatedDirectoryAnchor();
            if (AfterSourceStateCreatedForTestAsync != null)
            {
                await AfterSourceStateCreatedForTestAsync();
            }

            initialSource.MoveTo(sourceState, "source.claim");
            initialSource.Dispose();
            using var sourceClaim = sourceState.OpenExistingFile(
                "source.claim",
                requireDeleteAccess: true);
            var sourceSnapshot = await CaptureFileMoveContentAsync(sourceClaim);
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
            }
            using var destinationStage = destinationState.CreateNewFile(
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
            var destinationMatches = await FileMatchesMoveContentAsync(
                destinationStage,
                sourceSnapshot);
            if (!sourceStillMatches || !destinationMatches)
            {
                throw new IOException(
                    "The verified source or destination stage changed before source retirement.");
            }

            using var generationFence = sourceState.CreateNewFile(
                "replacement-generation.fence");
            await WriteFileMoveContentAsync(generationFence, sourceSnapshot);
            sourceRetirementCommitted = true;
            if (AfterSourceRetirementCommittedForTestAsync != null)
            {
                await AfterSourceRetirementCommittedForTestAsync();
            }

            sourceClaim.Delete(immediateWindows: true);
            sourceClaim.Dispose();
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
            destinationStage.MoveTo(
                lease.DestinationParent,
                lease.DestinationName);
            if (AfterDestinationPublishedForTestAsync != null)
            {
                await AfterDestinationPublishedForTestAsync(destinationFile);
            }

            using var previous = destinationState.TryOpenExistingFile(
                "destination.previous",
                requireDeleteAccess: true);
            previous?.Delete(immediateWindows: true);
            previous?.Dispose();
            using var recreatedSource = lease.SourceParent.TryOpenExistingFile(
                lease.SourceName,
                requireDeleteAccess: false);
            var sourcePathWasRecreated = recreatedSource != null;
            if (!sourcePathWasRecreated)
            {
                generationFence.Delete(immediateWindows: true);
                generationFence.Dispose();
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
            if (AfterFileMoveStateCleanedForTestAsync != null)
            {
                await AfterFileMoveStateCleanedForTestAsync();
            }

            return VerifiedFileMoveRemovalOutcome.Removed;
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
                _ = await TryRecoverInterruptedFileMoveClaimsAsync(lease);
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

    private async Task<FileMoveClaimRecoveryOutcome> TryRecoverInterruptedFileMoveClaimsAsync(
        FileMoveGateLease lease)
    {
        var sourceFile = lease.SourcePath;
        var destinationFile = lease.DestinationPath;
        var sourceIdentity = lease.SourceIdentity;
        var destinationIdentity = lease.DestinationIdentity;
        if (!lease.SourceParent.VisiblePathMatches()
            || !lease.DestinationParent.VisiblePathMatches())
        {
            return FileMoveClaimRecoveryOutcome.Blocked;
        }
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
            using var reverseSource =
                lease.DestinationParent.TryOpenExistingChildForPublication(
                    Path.GetFileName(reverseState.SourceStateDirectory));
            using var reverseDestination =
                lease.SourceParent.TryOpenExistingChildForPublication(
                    Path.GetFileName(reverseState.DestinationStateDirectory));
            if (reverseSource != null || reverseDestination != null)
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }
        }

        using var sourceStatePublication =
            lease.SourceParent.TryOpenExistingChildForPublication(
                Path.GetFileName(state.SourceStateDirectory));
        using var destinationStatePublication =
            lease.DestinationParent.TryOpenExistingChildForPublication(
                Path.GetFileName(state.DestinationStateDirectory));
        if (sourceStatePublication == null && destinationStatePublication == null)
        {
            return FileMoveClaimRecoveryOutcome.Ready;
        }

        try
        {
            using var sourceState =
                sourceStatePublication?.OpenCreatedDirectoryAnchor();
            using var destinationState =
                destinationStatePublication?.OpenCreatedDirectoryAnchor();
            if ((sourceState != null
                    && !AnchoredStateContainsOnly(
                        sourceState,
                        "source.claim",
                        "replacement-generation.fence"))
                || (destinationState != null
                    && !AnchoredStateContainsOnly(
                        destinationState,
                        "destination.stage",
                        "destination.previous")))
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }

            using var sourceClaim = sourceState?.TryOpenExistingFile(
                "source.claim",
                requireDeleteAccess: true);
            using var destinationStage = destinationState?.TryOpenExistingFile(
                "destination.stage",
                requireDeleteAccess: true);
            using var destinationPrevious = destinationState?.TryOpenExistingFile(
                "destination.previous",
                requireDeleteAccess: true);
            using var generationFence = sourceState?.TryOpenExistingFile(
                "replacement-generation.fence",
                requireDeleteAccess: true);

            if (generationFence == null)
            {
                if (sourceClaim != null)
                {
                    using var publicSource = lease.SourceParent.TryOpenExistingFile(
                        lease.SourceName,
                        requireDeleteAccess: false);
                    if (publicSource != null)
                    {
                        return FileMoveClaimRecoveryOutcome.Blocked;
                    }

                    sourceClaim.MoveTo(lease.SourceParent, lease.SourceName);
                    sourceClaim.Dispose();
                    destinationStage?.Delete(immediateWindows: true);
                    destinationStage?.Dispose();
                    if (destinationPrevious != null)
                    {
                        using var publicDestination =
                            lease.DestinationParent.TryOpenExistingFile(
                                lease.DestinationName,
                                requireDeleteAccess: false);
                        if (publicDestination != null)
                        {
                            return FileMoveClaimRecoveryOutcome.Blocked;
                        }
                        destinationPrevious.MoveTo(
                            lease.DestinationParent,
                            lease.DestinationName);
                        destinationPrevious.Dispose();
                    }

                    sourceState?.Dispose();
                    destinationState?.Dispose();
                    TryDeleteAnchoredStateDirectory(
                        sourceStatePublication,
                        Path.GetFileName(state.SourceStateDirectory));
                    TryDeleteAnchoredStateDirectory(
                        destinationStatePublication,
                        Path.GetFileName(state.DestinationStateDirectory));
                    return FileMoveClaimRecoveryOutcome.Ready;
                }

                if (destinationStage != null || destinationPrevious != null)
                {
                    return FileMoveClaimRecoveryOutcome.Blocked;
                }

                sourceState?.Dispose();
                destinationState?.Dispose();
                TryDeleteAnchoredStateDirectory(
                    sourceStatePublication,
                    Path.GetFileName(state.SourceStateDirectory));
                TryDeleteAnchoredStateDirectory(
                    destinationStatePublication,
                    Path.GetFileName(state.DestinationStateDirectory));
                using var liveSource = lease.SourceParent.TryOpenExistingFile(
                    lease.SourceName,
                    requireDeleteAccess: false);
                return liveSource != null
                    ? FileMoveClaimRecoveryOutcome.Ready
                    : FileMoveClaimRecoveryOutcome.Blocked;
            }

            var committedContent = await ReadFileMoveContentAsync(generationFence);
            if (!committedContent.HasValue)
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }

            if (sourceClaim != null)
            {
                if (destinationStage == null)
                {
                    return FileMoveClaimRecoveryOutcome.Blocked;
                }
                if (!await FileMatchesMoveContentAsync(
                        sourceClaim,
                        committedContent.Value)
                    || !await FileMatchesMoveContentAsync(
                        destinationStage,
                        committedContent.Value))
                {
                    return FileMoveClaimRecoveryOutcome.Blocked;
                }
                sourceClaim.Delete(immediateWindows: true);
                sourceClaim.Dispose();
            }

            if (destinationStage != null)
            {
                if (!await FileMatchesMoveContentAsync(
                        destinationStage,
                        committedContent.Value))
                {
                    return FileMoveClaimRecoveryOutcome.Blocked;
                }
                using var publicDestination =
                    lease.DestinationParent.TryOpenExistingFile(
                        lease.DestinationName,
                        requireDeleteAccess: false);
                if (publicDestination != null)
                {
                    return FileMoveClaimRecoveryOutcome.Blocked;
                }
                destinationStage.MoveTo(
                    lease.DestinationParent,
                    lease.DestinationName);
                destinationStage.Dispose();
            }

            using var publishedDestination =
                lease.DestinationParent.TryOpenExistingFile(
                    lease.DestinationName,
                    requireDeleteAccess: false);
            if (publishedDestination == null)
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }
            if (!await FileMatchesMoveContentAsync(
                    publishedDestination,
                    committedContent.Value))
            {
                return FileMoveClaimRecoveryOutcome.Blocked;
            }

            destinationPrevious?.Delete(immediateWindows: true);
            destinationPrevious?.Dispose();
            using var publishedSource = lease.SourceParent.TryOpenExistingFile(
                lease.SourceName,
                requireDeleteAccess: false);
            if (publishedSource == null)
            {
                generationFence.Delete(immediateWindows: true);
                generationFence.Dispose();
            }
            sourceState?.Dispose();
            destinationState?.Dispose();
            TryDeleteAnchoredStateDirectory(
                sourceStatePublication,
                Path.GetFileName(state.SourceStateDirectory));
            TryDeleteAnchoredStateDirectory(
                destinationStatePublication,
                Path.GetFileName(state.DestinationStateDirectory));
            return FileMoveClaimRecoveryOutcome.Completed;
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
