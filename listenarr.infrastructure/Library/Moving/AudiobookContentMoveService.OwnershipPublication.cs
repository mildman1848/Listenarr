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

        using var markerDirectoryAnchor =
            PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(markerDirectory);
        await PublishPinnedOwnershipMarkerAsync(
            markerDirectoryAnchor,
            markerPath,
            writePath,
            payload,
            marker,
            markerKind,
            authorizeMutation);
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
            exception =>
            {
                if (exception is MoveLeaseLostException or PersistenceException)
                {
                    return true;
                }

                faultInjector?.OnOwnershipMarkerWrite(
                    marker.JobId,
                    markerKind,
                    OwnershipMarkerWriteFaultPoint.BeforeTemporaryFileDeletion);
                return false;
            });
    }
}
