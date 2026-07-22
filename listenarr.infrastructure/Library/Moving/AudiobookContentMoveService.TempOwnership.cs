using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private const string TempOwnershipMarkerFileName = ".listenarr-temp-owner.json";

    private sealed record ValidatedTempOwnership(
        string DirectoryPath,
        string MarkerPath,
        MoveOwnershipMarker Marker);

    private async Task<ValidatedTempOwnership> CreateOrValidateOwnedTempDirectoryAsync(
        string tempDirectory,
        string targetParent,
        AudiobookContentMoveRequest request,
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        var markerPath = Path.Join(tempDirectory, TempOwnershipMarkerFileName);
        if (await TryCompleteOwnedDirectoryCleanupAsync(
                tempDirectory,
                markerPath,
                TemporaryDirectoryArtifactType,
                request.JobId,
                source,
                target,
                request.SourceSemantics,
                request.TargetSemantics,
                request.TargetSemantics,
                request.LeaseToken,
                () => EnsureMutationAuthorizedAsync(request, source, target, cancellationToken)))
        {
            if (Directory.Exists(tempDirectory) || File.Exists(tempDirectory))
            {
                throw new MoveNeedsAttentionException(
                    "The original move temporary path was recreated during cleanup and was preserved.");
            }

            // A prior cleanup completed. A new temp directory may now be claimed.
        }
        else if (Directory.Exists(tempDirectory))
        {
            try
            {
                return await ValidateOwnedTempDirectoryAsync(
                    tempDirectory,
                    targetParent,
                    request,
                    source,
                    target,
                    cancellationToken);
            }
            catch (InterruptedOwnershipPublicationException)
            {
                await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
                ValidateExistingMoveDirectory(tempDirectory, "interrupted temporary directory");
                if (Directory.EnumerateFileSystemEntries(tempDirectory).Any())
                {
                    throw new MoveNeedsAttentionException(
                        "An interrupted temporary ownership publication left unexpected content.");
                }

                Directory.Delete(tempDirectory, recursive: false);
            }
        }

        if (File.Exists(tempDirectory))
        {
            throw new MoveNeedsAttentionException(
                "The move temporary path is occupied by a file and cannot be claimed safely.");
        }

        ValidateMoveRootPath(tempDirectory, mustExist: false, "temporary directory");
        var normalizedTargetParent = Path.GetFullPath(targetParent);
        var normalizedTempDirectory = Path.GetFullPath(tempDirectory);
        var tempParent = Path.GetDirectoryName(normalizedTempDirectory);
        if (string.IsNullOrWhiteSpace(tempParent)
            || !FileSystemPathIdentity.AreEquivalent(
                normalizedTargetParent,
                tempParent,
                request.TargetSemantics))
        {
            throw new MoveNeedsAttentionException(
                "The move temporary directory escaped its validated target parent.");
        }

        await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
        ValidateMoveRootPath(tempDirectory, mustExist: false, "temporary directory");
        using var tempCreation = PinnedDirectoryCreation.TryCreate(
            normalizedTargetParent,
            Path.GetFileName(normalizedTempDirectory));
        if (!tempCreation.Created)
        {
            throw new MoveNeedsAttentionException(
                "The move temporary directory appeared before Listenarr could claim it exclusively.");
        }
        if (!tempCreation.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The move temporary directory parent changed during exclusive creation.");
        }

        using var tempAnchor = tempCreation.OpenCreatedDirectoryAnchor();
        if (!tempAnchor.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The move temporary directory identity changed after exclusive creation.");
        }

        ValidateExistingMoveDirectory(tempDirectory, "temporary directory");
        var marker = CreateOwnershipMarker(
            TemporaryDirectoryArtifactType,
            request.JobId,
            source,
            target,
            tempDirectory);
        try
        {
            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            await PublishOwnershipMarkerAsync(
                markerPath,
                marker,
                OwnershipMarkerKind.TemporaryDirectory,
                request.LeaseToken,
                () => EnsureMutationAuthorizedAsync(request, source, target, cancellationToken),
                tempAnchor);
            return await ValidateOwnedTempDirectoryAsync(
                tempDirectory,
                targetParent,
                request,
                source,
                target,
                cancellationToken);
        }
        catch (Exception exception) when (exception is MoveLeaseLostException or PersistenceException)
        {
            throw;
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await TryRemoveNewEmptyOwnershipDirectoryAsync(
                tempDirectory,
                request.JobId,
                "temp",
                () => EnsureMutationAuthorizedAsync(request, source, target, cancellationToken));
            throw;
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            await TryRemoveNewEmptyOwnershipDirectoryAsync(
                tempDirectory,
                request.JobId,
                "temp",
                () => EnsureMutationAuthorizedAsync(request, source, target, cancellationToken));
            throw new MoveNeedsAttentionException(
                $"The move temporary directory could not be claimed safely: {exception.Message}");
        }
    }

    private async Task<ValidatedTempOwnership> ValidateOwnedTempDirectoryAsync(
        string tempDirectory,
        string targetParent,
        AudiobookContentMoveRequest request,
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        if (!FileSystemSafety.TryValidateMutationTarget(
                tempDirectory,
                [targetParent],
                out var safeTempDirectory,
                out var tempReason))
        {
            throw new MoveNeedsAttentionException(tempReason);
        }

        ValidateExistingMoveDirectory(safeTempDirectory, "temporary directory");
        var markerPath = Path.Join(safeTempDirectory, TempOwnershipMarkerFileName);
        var expectedMarker = CreateOwnershipMarker(
            TemporaryDirectoryArtifactType,
            request.JobId,
            source,
            target,
            tempDirectory);
        var marker = await RecoverOrReadOwnershipMarkerAsync(
            markerPath,
            expectedMarker,
            request.SourceSemantics,
            request.TargetSemantics,
            request.TargetSemantics,
            request.LeaseToken,
            () => EnsureMutationAuthorizedAsync(request, source, target, cancellationToken));
        return new ValidatedTempOwnership(
            safeTempDirectory,
            markerPath,
            marker);
    }

    private async Task TryDeleteOwnedTempDirectoryAsync(
        string tempDirectory,
        string targetParent,
        AudiobookContentMoveRequest request,
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        var markerPath = Path.Join(tempDirectory, TempOwnershipMarkerFileName);
        try
        {
            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            if (await TryCompleteOwnedDirectoryCleanupAsync(
                    tempDirectory,
                    markerPath,
                    TemporaryDirectoryArtifactType,
                    request.JobId,
                    source,
                    target,
                    request.SourceSemantics,
                    request.TargetSemantics,
                    request.TargetSemantics,
                    request.LeaseToken,
                    () => EnsureMutationAuthorizedAsync(request, source, target, cancellationToken)))
            {
                return;
            }

            if (!Directory.Exists(tempDirectory))
            {
                return;
            }

            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            var ownership = await ValidateOwnedTempDirectoryAsync(
                tempDirectory,
                targetParent,
                request,
                source,
                target,
                cancellationToken);
            await DeleteOwnedDirectoryWithTombstoneAsync(
                ownership.DirectoryPath,
                ownership.MarkerPath,
                TemporaryDirectoryArtifactType,
                request.JobId,
                source,
                target,
                request.SourceSemantics,
                request.TargetSemantics,
                request.TargetSemantics,
                request.LeaseToken,
                () => EnsureMutationAuthorizedAsync(request, source, target, cancellationToken));
        }
        catch (MoveLeaseLostException)
        {
            throw;
        }
        catch (PersistenceException)
        {
            throw;
        }
        catch (MoveNeedsAttentionException exception)
        {
            logger.LogWarning(
                exception,
                "Preserved unowned or ambiguous move temp directory for job {JobId}",
                request.JobId);
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            logger.LogWarning(
                exception,
                "Failed to clean the validated move temp directory for job {JobId}",
                request.JobId);
        }
    }

    private async Task<ValidatedTempOwnership?> TryValidatePublishedTempOwnershipAsync(
        string destinationRoot,
        AudiobookContentMoveRequest request,
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        var markerPath = Path.Join(destinationRoot, TempOwnershipMarkerFileName);
        if (!File.Exists(markerPath))
        {
            return null;
        }

        var destinationParent = Path.GetDirectoryName(destinationRoot)
            ?? throw new MoveNeedsAttentionException("The destination parent is unavailable.");
        var originalTempDirectory = Path.Join(
            destinationParent,
            Path.GetFileName(target) + ".tmp-" + request.JobId.ToString("N"));
        if (!FileSystemSafety.TryValidateMutationTarget(
                destinationRoot,
                [destinationParent],
                out var safeDestination,
                out var destinationReason))
        {
            throw new MoveNeedsAttentionException(destinationReason);
        }

        ValidateExistingMoveDirectory(safeDestination, "published temporary directory");
        var expectedMarker = CreateOwnershipMarker(
            TemporaryDirectoryArtifactType,
            request.JobId,
            source,
            target,
            originalTempDirectory);
        var marker = await RecoverOrReadOwnershipMarkerAsync(
            markerPath,
            expectedMarker,
            request.SourceSemantics,
            request.TargetSemantics,
            request.TargetSemantics,
            request.LeaseToken,
            () => EnsureMutationAuthorizedAsync(request, source, target, cancellationToken));
        return new ValidatedTempOwnership(
            safeDestination,
            markerPath,
            marker);
    }

    private async Task TryDeletePublishedTempOwnershipMarkerAsync(
        ValidatedTempOwnership? ownership,
        AudiobookContentMoveRequest request,
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        if (ownership == null || !File.Exists(ownership.MarkerPath))
        {
            return;
        }

        ValidateExistingMoveDirectory(
            ownership.DirectoryPath,
            "published temporary directory");
        var marker = ReadOwnershipMarker(ownership.MarkerPath);
        ValidateOwnershipMarker(
            marker,
            ownership.Marker,
            request.SourceSemantics,
            request.TargetSemantics,
            request.TargetSemantics);
        ValidateExistingMoveDirectory(
            ownership.DirectoryPath,
            "published temporary directory");
        var currentMarker = ReadOwnershipMarker(ownership.MarkerPath);
        ValidateOwnershipMarker(
            currentMarker,
            ownership.Marker,
            request.SourceSemantics,
            request.TargetSemantics,
            request.TargetSemantics);
        await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
        ValidateExistingMoveDirectory(
            ownership.DirectoryPath,
            "published temporary directory");
        currentMarker = ReadOwnershipMarker(ownership.MarkerPath);
        ValidateOwnershipMarker(
            currentMarker,
            ownership.Marker,
            request.SourceSemantics,
            request.TargetSemantics,
            request.TargetSemantics);
        File.Delete(ownership.MarkerPath);
    }

    private async Task TryRemoveNewEmptyOwnershipDirectoryAsync(
        string directory,
        Guid jobId,
        string artifactName,
        Func<Task> authorizeMutation)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                ValidateExistingMoveDirectory(
                    directory,
                    $"new {artifactName} directory");
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    await authorizeMutation();
                    ValidateExistingMoveDirectory(
                        directory,
                        $"new {artifactName} directory");
                    if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        Directory.Delete(directory, recursive: false);
                    }
                }
            }
        }
        catch (Exception cleanupException) when (cleanupException is
            MoveLeaseLostException or PersistenceException)
        {
            throw;
        }
        catch (Exception cleanupException) when (WorkerExceptionClassifier.IsNonFatal(cleanupException))
        {
            logger.LogWarning(
                cleanupException,
                "Failed to remove newly created empty {ArtifactName} directory for move job {JobId}",
                artifactName,
                jobId);
        }
    }
}
