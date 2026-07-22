using System.Text.Json;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task PublishOwnershipMarkerAsync(
        string markerPath,
        MoveOwnershipMarker marker,
        OwnershipMarkerKind markerKind,
        MoveLeaseToken leaseToken,
        Func<Task> authorizeMutation,
        PinnedDirectoryCreation.PinnedDirectoryAnchor? pinnedDirectory = null)
    {
        var markerDirectory = Path.GetDirectoryName(Path.GetFullPath(markerPath))
            ?? throw new MoveNeedsAttentionException("The ownership marker directory is unavailable.");
        ValidateExistingMoveDirectory(markerDirectory, "ownership-marker directory");
        if (!FileSystemSafety.TryValidateMutationTarget(
                markerPath,
                [markerDirectory],
                out markerPath,
                out var markerReason))
        {
            throw new MoveNeedsAttentionException(markerReason);
        }

        if (File.Exists(markerPath))
        {
            throw new MoveNeedsAttentionException(
                "The ownership marker already exists and cannot be overwritten safely.");
        }

        var payload = JsonSerializer.SerializeToUtf8Bytes(marker);
        var writePath = CreateMarkerWritePath(
            markerPath,
            marker.JobId,
            leaseToken.Generation);
        if (pinnedDirectory != null)
        {
            await PublishPinnedOwnershipMarkerAsync(
                pinnedDirectory,
                markerPath,
                writePath,
                payload,
                marker,
                markerKind,
                authorizeMutation);
            return;
        }

        try
        {
            await authorizeMutation();
            ValidateNewOwnershipMarkerWritePath(writePath, markerDirectory);
            faultInjector?.OnOwnershipMarkerWrite(
                marker.JobId,
                markerKind,
                OwnershipMarkerWriteFaultPoint.BeforeTemporaryFileCreation);
            using (var stream = new FileStream(
                writePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                var split = Math.Max(1, payload.Length / 2);
                stream.Write(payload.AsSpan(0, split));
                faultInjector?.OnOwnershipMarkerWrite(
                    marker.JobId,
                    markerKind,
                    OwnershipMarkerWriteFaultPoint.DuringJsonWrite);
                stream.Write(payload.AsSpan(split));
                faultInjector?.OnOwnershipMarkerWrite(
                    marker.JobId,
                    markerKind,
                    OwnershipMarkerWriteFaultPoint.DuringFlush);
                await authorizeMutation();
                stream.Flush(flushToDisk: true);
            }

            faultInjector?.OnOwnershipMarkerWrite(
                marker.JobId,
                markerKind,
                OwnershipMarkerWriteFaultPoint.AfterTemporaryFileWritten);
            faultInjector?.OnOwnershipMarkerWrite(
                marker.JobId,
                markerKind,
                OwnershipMarkerWriteFaultPoint.BeforePublication);
            await authorizeMutation();

            if (OperatingSystem.IsWindows())
            {
                File.SetAttributes(
                    writePath,
                    File.GetAttributes(writePath) | FileAttributes.Hidden);
            }

            ValidateOwnershipMarkerPublicationPaths(
                markerDirectory,
                writePath,
                markerPath);
            await authorizeMutation();
            ValidateOwnershipMarkerPublicationPaths(
                markerDirectory,
                writePath,
                markerPath);
            File.Move(writePath, markerPath, overwrite: false);
        }
        catch (Exception exception) when (exception is MoveLeaseLostException or PersistenceException)
        {
            throw;
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            Exception? cleanupException = null;
            try
            {
                faultInjector?.OnOwnershipMarkerWrite(
                    marker.JobId,
                    markerKind,
                    OwnershipMarkerWriteFaultPoint.BeforeTemporaryFileDeletion);
                if (File.Exists(writePath))
                {
                    await authorizeMutation();
                    ValidateOwnershipMarkerWritePath(writePath, markerDirectory);
                    File.Delete(writePath);
                }
            }
            catch (Exception temporaryCleanupException) when (temporaryCleanupException is
                MoveLeaseLostException or PersistenceException)
            {
                throw;
            }
            catch (Exception temporaryCleanupException) when (WorkerExceptionClassifier.IsNonFatal(temporaryCleanupException))
            {
                cleanupException = temporaryCleanupException;
            }

            if (exception is MoveNeedsAttentionException)
            {
                throw;
            }

            if (cleanupException is MoveNeedsAttentionException)
            {
                throw new MoveNeedsAttentionException(
                    $"Ownership marker publication failed and its temporary file became ambiguous. "
                    + $"Publication error: {exception.Message}. "
                    + $"Temporary cleanup error: {cleanupException.Message}.");
            }

            if (cleanupException != null)
            {
                throw new IOException(
                    $"Ownership marker publication failed and its validated temporary file could not be removed. "
                    + $"Publication error: {exception.Message}. "
                    + $"Temporary cleanup error: {cleanupException.Message}.",
                    cleanupException);
            }

            throw;
        }
    }

    private async Task PublishPinnedOwnershipMarkerAsync(
        PinnedDirectoryCreation.PinnedDirectoryAnchor pinnedDirectory,
        string markerPath,
        string writePath,
        byte[] payload,
        MoveOwnershipMarker marker,
        OwnershipMarkerKind markerKind,
        Func<Task> authorizeMutation)
    {
        await pinnedDirectory.PublishNewFileAsync(
            Path.GetFileName(writePath),
            Path.GetFileName(markerPath),
            async () =>
            {
                faultInjector?.OnOwnershipMarkerWrite(
                    marker.JobId,
                    markerKind,
                    OwnershipMarkerWriteFaultPoint.BeforeTemporaryFileCreation);
                await authorizeMutation();
            },
            async stream =>
            {
                var split = Math.Max(1, payload.Length / 2);
                stream.Write(payload.AsSpan(0, split));
                faultInjector?.OnOwnershipMarkerWrite(
                    marker.JobId,
                    markerKind,
                    OwnershipMarkerWriteFaultPoint.DuringJsonWrite);
                stream.Write(payload.AsSpan(split));
                faultInjector?.OnOwnershipMarkerWrite(
                    marker.JobId,
                    markerKind,
                    OwnershipMarkerWriteFaultPoint.DuringFlush);
                await authorizeMutation();
                stream.Flush(flushToDisk: true);
            },
            async () =>
            {
                faultInjector?.OnOwnershipMarkerWrite(
                    marker.JobId,
                    markerKind,
                    OwnershipMarkerWriteFaultPoint.AfterTemporaryFileWritten);
                faultInjector?.OnOwnershipMarkerWrite(
                    marker.JobId,
                    markerKind,
                    OwnershipMarkerWriteFaultPoint.BeforePublication);
                await authorizeMutation();
            },
            exception => exception is MoveLeaseLostException or PersistenceException);
    }
}
