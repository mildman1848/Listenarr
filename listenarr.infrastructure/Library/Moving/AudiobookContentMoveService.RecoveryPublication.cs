using System.Text.Json;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task WriteRecoveryMarkerAsync(
        string markerDirectory,
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string stage,
        CancellationToken cancellationToken)
    {
        ValidateExistingMoveDirectory(markerDirectory, "recovery-marker directory");
        var markerPath = GetRecoveryMarkerPath(markerDirectory, request.JobId);
        if (!FileSystemSafety.TryValidateMutationTarget(
                markerPath,
                [markerDirectory],
                out markerPath,
                out var markerReason))
        {
            throw new MoveNeedsAttentionException(markerReason);
        }

        var marker = new MoveRecoveryMarker(
            RecoveryMarkerVersion,
            request.JobId,
            Path.GetFullPath(source),
            Path.GetFullPath(target),
            stage);
        var payload = JsonSerializer.SerializeToUtf8Bytes(marker);
        var writePath = CreateMarkerWritePath(
            markerPath,
            request.JobId,
            request.LeaseGeneration);
        var retiredExistingMarker = false;
        PinnedDirectoryCreation.PinnedDirectoryAnchor? markerParent = null;
        PinnedDirectoryCreation.PinnedFileEntry? writeEntry = null;

        faultInjector?.OnRecoveryMarkerWrite(
            request.JobId,
            RecoveryMarkerWriteFaultPoint.BeforeTemporaryFileCreation);

        try
        {
            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            ValidateNewRecoveryMarkerWritePath(writePath, markerDirectory);
            markerParent = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(
                markerDirectory);
            writeEntry = markerParent.CreateNewFile(
                Path.GetFileName(writePath),
                hiddenFile: OperatingSystem.IsWindows());
            using (var stream = writeEntry.OpenWriteStream(
                bufferSize: 4096,
                asynchronous: false))
            {
                var split = Math.Max(1, payload.Length / 2);
                stream.Write(payload.AsSpan(0, split));
                faultInjector?.OnRecoveryMarkerWrite(
                    request.JobId,
                    RecoveryMarkerWriteFaultPoint.DuringJsonWrite);
                stream.Write(payload.AsSpan(split));
                faultInjector?.OnRecoveryMarkerWrite(
                    request.JobId,
                    RecoveryMarkerWriteFaultPoint.DuringFlush);
                await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            faultInjector?.OnRecoveryMarkerWrite(
                request.JobId,
                RecoveryMarkerWriteFaultPoint.AfterTemporaryFileWritten);
            faultInjector?.OnRecoveryMarkerWrite(
                request.JobId,
                RecoveryMarkerWriteFaultPoint.BeforePublication);
            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);

            ValidateRecoveryMarkerPublicationPaths(
                markerDirectory,
                writePath,
                markerPath);
            var candidate = ReadRecoveryMarker(writeEntry, writePath);
            ValidateRecoveryMarker(candidate, request, source, target);
            if (!string.Equals(candidate.Stage, stage, StringComparison.Ordinal))
            {
                throw new MoveNeedsAttentionException(
                    "The recovery-marker write file changed before publication.");
            }

            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            if (File.Exists(markerPath))
            {
                using (var existingEntry = markerParent.OpenExistingFile(
                    Path.GetFileName(markerPath),
                    requireDeleteAccess: true))
                {
                    var existing = ReadRecoveryMarker(existingEntry, markerPath);
                    ValidateRecoveryMarker(existing, request, source, target);
                    if (!CanAdvanceRecoveryStage(existing.Stage, stage))
                    {
                        throw new MoveNeedsAttentionException(
                            "The existing recovery marker is already at a later or incompatible stage.");
                    }
                    if (!markerParent.VisiblePathMatches()
                        || !writeEntry.VisiblePathMatches()
                        || !existingEntry.VisiblePathMatches())
                    {
                        throw new MoveNeedsAttentionException(
                            "Recovery-marker publication paths changed at the mutation boundary.");
                    }

                    existingEntry.Delete();
                }
                retiredExistingMarker = true;
            }

            if (!markerParent.VisiblePathMatches()
                || !writeEntry.VisiblePathMatches())
            {
                throw new MoveNeedsAttentionException(
                    "The recovery-marker write file changed before publication.");
            }
            writeEntry.MoveWithinParent(Path.GetFileName(markerPath));
        }
        catch (Exception exception) when (exception is MoveLeaseLostException or PersistenceException)
        {
            throw;
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            Exception? cleanupException = null;
            if (!retiredExistingMarker)
            {
                try
                {
                    faultInjector?.OnRecoveryMarkerWrite(
                        request.JobId,
                        RecoveryMarkerWriteFaultPoint.BeforeTemporaryFileDeletion);
                    if (File.Exists(writePath))
                    {
                        await EnsureMutationAuthorizedAsync(
                            request,
                            source,
                            target,
                            cancellationToken);
                        if (markerParent == null
                            || writeEntry == null
                            || !markerParent.VisiblePathMatches()
                            || !writeEntry.VisiblePathMatches())
                        {
                            throw new MoveNeedsAttentionException(
                                "The recovery-marker write file changed before cleanup.");
                        }

                        writeEntry.Delete();
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
            }

            if (exception is MoveNeedsAttentionException)
            {
                throw;
            }

            if (cleanupException is MoveNeedsAttentionException)
            {
                throw new MoveNeedsAttentionException(
                    $"Recovery marker publication failed and recovery state became ambiguous. "
                    + $"Publication error: {exception.Message}. "
                    + $"Temporary cleanup error: {cleanupException?.Message ?? "none"}.");
            }

            if (cleanupException != null)
            {
                throw new IOException(
                    $"Recovery marker publication failed and its validated recovery state could not be restored cleanly. "
                    + $"Publication error: {exception.Message}. "
                    + $"Temporary cleanup error: {cleanupException?.Message ?? "none"}.",
                    cleanupException);
            }

            throw;
        }
        finally
        {
            writeEntry?.Dispose();
            markerParent?.Dispose();
        }
    }
}
