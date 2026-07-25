using System.Security.Cryptography;

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
}
