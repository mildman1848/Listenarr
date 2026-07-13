namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private const long MaximumMarkerLength = 64 * 1024;

    private enum MarkerReadState
    {
        Missing,
        Valid,
        CorruptOrTruncated,
        TemporarilyUnreadable,
        Unsupported
    }

    private readonly record struct MarkerReadResult<T>(
        MarkerReadState State,
        T? Marker = default,
        Exception? Error = null);

    private readonly record struct MarkerWriteIdentity(
        Guid JobId,
        int LeaseGeneration);

    private sealed class InterruptedOwnershipPublicationException(string message)
        : IOException(message);

    private static MarkerReadResult<T> ReadJsonMarker<T>(string path)
    {
        if (!File.Exists(path))
        {
            return new MarkerReadResult<T>(MarkerReadState.Missing);
        }

        try
        {
            var fileInfo = new FileInfo(path);
            if (fileInfo.Length > MaximumMarkerLength)
            {
                return new MarkerReadResult<T>(MarkerReadState.CorruptOrTruncated);
            }

            var marker = System.Text.Json.JsonSerializer.Deserialize<T>(
                File.ReadAllText(path));
            return marker == null
                ? new MarkerReadResult<T>(MarkerReadState.CorruptOrTruncated)
                : new MarkerReadResult<T>(MarkerReadState.Valid, marker);
        }
        catch (System.Text.Json.JsonException exception)
        {
            return new MarkerReadResult<T>(
                MarkerReadState.CorruptOrTruncated,
                Error: exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new MarkerReadResult<T>(
                MarkerReadState.TemporarilyUnreadable,
                Error: exception);
        }
    }

    private static string CreateMarkerWritePath(
        string markerPath,
        Guid jobId,
        int leaseGeneration) =>
        markerPath
        + $".writing-{jobId:N}-g{leaseGeneration}-{Guid.NewGuid():N}";

    private static bool TryParseMarkerWriteIdentity(
        string writePath,
        string markerPath,
        out MarkerWriteIdentity identity)
    {
        identity = default;
        var fileName = Path.GetFileName(writePath);
        var prefix = Path.GetFileName(markerPath) + ".writing-";
        if (!fileName.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = fileName[prefix.Length..];
        var generationSeparator = suffix.IndexOf("-g", StringComparison.Ordinal);
        if (generationSeparator != 32
            || !Guid.TryParseExact(suffix[..generationSeparator], "N", out var jobId))
        {
            return false;
        }

        var uniqueSeparator = suffix.IndexOf('-', generationSeparator + 2);
        if (uniqueSeparator <= generationSeparator + 2
            || !int.TryParse(
                suffix.AsSpan(generationSeparator + 2, uniqueSeparator - generationSeparator - 2),
                out var leaseGeneration)
            || leaseGeneration <= 0
            || !Guid.TryParseExact(suffix[(uniqueSeparator + 1)..], "N", out _))
        {
            return false;
        }

        identity = new MarkerWriteIdentity(jobId, leaseGeneration);
        return true;
    }
}
