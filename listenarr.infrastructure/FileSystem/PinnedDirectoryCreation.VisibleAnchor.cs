namespace Listenarr.Infrastructure.FileSystem;

internal sealed partial class PinnedDirectoryCreation
{
    internal static PinnedDirectoryAnchor OpenPinnedVisibleDirectory(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        var handle = OpenVisibleDirectory(directoryPath);
        var anchor = new PinnedDirectoryAnchor(
            handle,
            directoryPath,
            followVisibleFinalLink: false);
        if (anchor.VisiblePathMatches())
        {
            return anchor;
        }

        anchor.Dispose();
        throw new InvalidOperationException(
            "The visible directory changed while its identity was being pinned.");
    }
}
