namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private static MoveOwnershipMarker ReadOwnershipMarker(
        PinnedDirectoryCreation.PinnedFileEntry markerEntry,
        string markerPath)
    {
        var result = ReadOwnershipMarkerResult(markerEntry);
        return result.State switch
        {
            MarkerReadState.Valid => result.Marker!,
            MarkerReadState.TemporarilyUnreadable => throw new IOException(
                $"The ownership marker '{markerPath}' is temporarily unreadable.",
                result.Error),
            MarkerReadState.Unsupported => throw new MoveNeedsAttentionException(
                "The ownership marker uses an unsupported marker version and was preserved."),
            MarkerReadState.CorruptOrTruncated => throw new MoveNeedsAttentionException(
                "The ownership marker is corrupt or truncated."),
            _ => throw new MoveNeedsAttentionException("The ownership marker is missing.")
        };
    }

    private static MarkerReadResult<MoveOwnershipMarker> ReadOwnershipMarkerResult(
        PinnedDirectoryCreation.PinnedFileEntry markerEntry)
    {
        try
        {
            using var stream = markerEntry.OpenReadStream(
                bufferSize: 4096,
                asynchronous: false);
            if (stream.Length > MaximumMarkerLength)
            {
                return new MarkerReadResult<MoveOwnershipMarker>(
                    MarkerReadState.CorruptOrTruncated);
            }

            stream.Position = 0;
            var marker = System.Text.Json.JsonSerializer.Deserialize<MoveOwnershipMarker>(
                stream);
            if (marker == null)
            {
                return new MarkerReadResult<MoveOwnershipMarker>(
                    MarkerReadState.CorruptOrTruncated);
            }
            return marker.Version == OwnershipMarkerVersion
                ? new MarkerReadResult<MoveOwnershipMarker>(
                    MarkerReadState.Valid,
                    marker)
                : new MarkerReadResult<MoveOwnershipMarker>(
                    MarkerReadState.Unsupported,
                    marker);
        }
        catch (System.Text.Json.JsonException exception)
        {
            return new MarkerReadResult<MoveOwnershipMarker>(
                MarkerReadState.CorruptOrTruncated,
                Error: exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new MarkerReadResult<MoveOwnershipMarker>(
                MarkerReadState.TemporarilyUnreadable,
                Error: exception);
        }
    }
}
