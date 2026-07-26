using System.Security.Cryptography;
using System.Text;

namespace Listenarr.Infrastructure.FileSystem;

public partial class FileMover
{
    private readonly record struct FileMoveContent(long Length, string Sha256);

    private static async Task<FileMoveContent> CaptureFileMoveContentAsync(
        string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read | FileShare.Delete,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = stream.Length;
        var hash = await SHA256.HashDataAsync(stream);
        return new FileMoveContent(length, Convert.ToHexString(hash));
    }

    private static async Task<FileMoveContent> CaptureFileMoveContentAsync(
        PinnedDirectoryCreation.PinnedFileEntry entry)
    {
        await using var stream = entry.OpenReadStream(
            bufferSize: 128 * 1024,
            asynchronous: false);
        var length = stream.Length;
        stream.Position = 0;
        var hash = await SHA256.HashDataAsync(stream);
        return new FileMoveContent(length, Convert.ToHexString(hash));
    }

    private static Task<bool> FileMatchesMoveContentAsync(
        PinnedDirectoryCreation.PinnedFileEntry entry,
        FileMoveContent expected) =>
        entry.MatchesAsync(
            expected.Length,
            expected.Sha256,
            CancellationToken.None);

    private static async Task<bool> FileMatchesMoveContentAsync(
        string path,
        FileMoveContent expected)
    {
        if (!File.Exists(path) || IsLinkedOrUnverifiableEntry(path))
        {
            return false;
        }

        var actual = await CaptureFileMoveContentAsync(path);
        return actual == expected;
    }

    private static async Task WriteFileMoveContentAsync(
        PinnedDirectoryCreation.PinnedFileEntry entry,
        FileMoveContent content)
    {
        var payload = Encoding.ASCII.GetBytes(
            $"{content.Length}\n{content.Sha256}\n");
        await using var stream = entry.OpenWriteStream(
            bufferSize: 256,
            asynchronous: false);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
        stream.Flush(flushToDisk: true);
    }

    private static async Task<FileMoveContent?> ReadFileMoveContentAsync(
        PinnedDirectoryCreation.PinnedFileEntry entry)
    {
        await using var stream = entry.OpenReadStream(
            bufferSize: 256,
            asynchronous: false);
        if (stream.Length is <= 0 or > 256)
        {
            return null;
        }

        var payload = new byte[stream.Length];
        var offset = 0;
        while (offset < payload.Length)
        {
            var read = await stream.ReadAsync(payload.AsMemory(offset));
            if (read == 0)
            {
                return null;
            }
            offset += read;
        }

        var parts = Encoding.ASCII.GetString(payload)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2
            || !long.TryParse(
                parts[0],
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var length)
            || length < 0
            || parts[1].Length != 64
            || parts[1].Any(character => !Uri.IsHexDigit(character)))
        {
            return null;
        }

        return new FileMoveContent(length, parts[1].ToUpperInvariant());
    }
}
