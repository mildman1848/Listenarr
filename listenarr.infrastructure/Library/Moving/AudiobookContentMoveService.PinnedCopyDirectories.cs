using System.ComponentModel;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task EnsurePinnedCopyDirectoryAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string destinationRoot,
        string relativePath,
        FileSystemPathSemantics targetSemantics,
        CancellationToken cancellationToken)
    {
        var segments = SplitPinnedRelativeDirectoryPath(
            relativePath,
            targetSemantics);
        try
        {
            using var destinationPath = await PinnedMoveDirectoryPath.OpenOrCreateAsync(
                destinationRoot,
                segments,
                () => EnsureMutationAuthorizedAsync(
                    request,
                    source,
                    target,
                    cancellationToken));
            destinationPath.EnsureVisibleHierarchy();
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            throw new MoveNeedsAttentionException(
                $"The copy destination directory could not be created through pinned handles: {exception.Message}");
        }
    }

    private static IReadOnlyList<string> SplitPinnedRelativeDirectoryPath(
        string relativePath,
        FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new MoveNeedsAttentionException(
                "A directory manifest entry has no relative path.");
        }

        var separators = semantics.Syntax == FileSystemPathSyntax.Windows
            ? new[] { '\\', '/' }
            : new[] { '/' };
        var segments = relativePath.Split(
            separators,
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0
            || segments.Any(segment => segment is "." or ".."))
        {
            throw new MoveNeedsAttentionException(
                "A directory manifest entry contains an invalid copy path segment.");
        }

        return segments;
    }
}
