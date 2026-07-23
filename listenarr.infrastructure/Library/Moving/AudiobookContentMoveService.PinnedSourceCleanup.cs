using System.ComponentModel;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task MoveSourceFileToPinnedQuarantineAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string sourceFile,
        string quarantineFile,
        string quarantineRoot,
        MoveJobEntry manifestEntry,
        FileSystemPathSemantics sourceSemantics,
        CancellationToken cancellationToken)
    {
        var (directorySegments, fileName) = SplitPinnedRelativeFilePath(
            manifestEntry.RelativePath,
            sourceSemantics);
        try
        {
            using var sourcePath = PinnedCleanupDirectoryPath.OpenExisting(
                source,
                directorySegments);
            using var quarantinePath = await PinnedCleanupDirectoryPath.OpenOrCreateAsync(
                quarantineRoot,
                directorySegments,
                () => EnsureMutationAuthorizedAsync(
                    request,
                    source,
                    target,
                    cancellationToken));
            ValidatePinnedParentPath(
                sourcePath.Current.FullPath,
                sourceFile,
                sourceSemantics,
                "source cleanup");
            ValidatePinnedParentPath(
                quarantinePath.Current.FullPath,
                quarantineFile,
                sourceSemantics,
                "quarantine cleanup");

            using var sourceEntry = sourcePath.Current.OpenExistingFile(
                fileName,
                requireDeleteAccess: true);
            if (!sourceEntry.VisiblePathMatches()
                || !await sourceEntry.MatchesAsync(
                    manifestEntry.Length,
                    manifestEntry.Sha256,
                    cancellationToken))
            {
                throw new MoveNeedsAttentionException(
                    $"The pinned source cleanup entry changed after validation: {manifestEntry.RelativePath}");
            }

            sourcePath.EnsureVisibleHierarchy();
            quarantinePath.EnsureVisibleHierarchy();
            faultInjector?.OnSourceCleanupMutation(
                request.JobId,
                SourceCleanupFaultPoint.BeforeSourceFilePublication);
            await EnsureMutationAuthorizedAsync(
                request,
                source,
                target,
                cancellationToken);
            sourcePath.EnsureVisibleHierarchy();
            quarantinePath.EnsureVisibleHierarchy();
            if (!sourceEntry.VisiblePathMatches()
                || !await sourceEntry.MatchesAsync(
                    manifestEntry.Length,
                    manifestEntry.Sha256,
                    cancellationToken))
            {
                throw new MoveNeedsAttentionException(
                    $"The pinned source cleanup entry changed at publication: {manifestEntry.RelativePath}");
            }

            sourceEntry.MoveTo(quarantinePath.Current, fileName);
            if (!await sourceEntry.MatchesAsync(
                    manifestEntry.Length,
                    manifestEntry.Sha256,
                    cancellationToken))
            {
                throw new MoveNeedsAttentionException(
                    $"The pinned quarantine publication changed bytes: {manifestEntry.RelativePath}");
            }
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            throw new MoveNeedsAttentionException(
                $"The source file could not be quarantined through pinned directory handles: {exception.Message}");
        }
    }

    private static void ValidatePinnedParentPath(
        string pinnedParent,
        string expectedFile,
        FileSystemPathSemantics semantics,
        string description)
    {
        var expectedParent = Path.GetDirectoryName(Path.GetFullPath(expectedFile));
        if (string.IsNullOrWhiteSpace(expectedParent)
            || !FileSystemPathIdentity.AreEquivalent(
                Path.GetFullPath(pinnedParent),
                expectedParent,
                semantics))
        {
            throw new MoveNeedsAttentionException(
                $"The pinned {description} parent does not match the manifest path.");
        }
    }

    private static (IReadOnlyList<string> DirectorySegments, string FileName)
        SplitPinnedRelativeFilePath(
            string relativePath,
            FileSystemPathSemantics semantics)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new MoveNeedsAttentionException(
                "A file manifest entry has no relative path.");
        }

        var separators = semantics.Syntax == FileSystemPathSyntax.Windows
            ? new[] { '\\', '/' }
            : new[] { '/' };
        var lastSeparator = relativePath.LastIndexOfAny(separators);
        var fileName = lastSeparator < 0
            ? relativePath
            : relativePath[(lastSeparator + 1)..];
        var directoryPart = lastSeparator < 0
            ? string.Empty
            : relativePath[..lastSeparator];
        var segments = directoryPart.Split(
            separators,
            StringSplitOptions.RemoveEmptyEntries);
        if (string.IsNullOrWhiteSpace(fileName)
            || segments.Any(segment => segment is "." or ".."))
        {
            throw new MoveNeedsAttentionException(
                "A file manifest entry contains an invalid cleanup path segment.");
        }

        return (segments, fileName);
    }

    private sealed class PinnedCleanupDirectoryPath : IDisposable
    {
        private readonly List<PinnedDirectoryCreation.PinnedDirectoryAnchor> _anchors;
        private bool _disposed;

        private PinnedCleanupDirectoryPath(
            List<PinnedDirectoryCreation.PinnedDirectoryAnchor> anchors)
        {
            _anchors = anchors;
        }

        internal PinnedDirectoryCreation.PinnedDirectoryAnchor Current
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                return _anchors[^1];
            }
        }

        internal static PinnedCleanupDirectoryPath OpenExisting(
            string root,
            IReadOnlyList<string> segments)
        {
            var anchors = new List<PinnedDirectoryCreation.PinnedDirectoryAnchor>();
            try
            {
                var current = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(root);
                anchors.Add(current);
                foreach (var segment in segments)
                {
                    current = current.OpenExistingChild(segment);
                    anchors.Add(current);
                }

                var path = new PinnedCleanupDirectoryPath(anchors);
                path.EnsureVisibleHierarchy();
                return path;
            }
            catch
            {
                DisposeAnchors(anchors);
                throw;
            }
        }

        internal static async Task<PinnedCleanupDirectoryPath> OpenOrCreateAsync(
            string root,
            IReadOnlyList<string> segments,
            Func<Task> authorizeMutation)
        {
            ArgumentNullException.ThrowIfNull(authorizeMutation);
            var anchors = new List<PinnedDirectoryCreation.PinnedDirectoryAnchor>();
            try
            {
                var current = PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(root);
                anchors.Add(current);
                foreach (var segment in segments)
                {
                    var childPath = Path.Join(current.FullPath, segment);
                    PinnedDirectoryCreation.PinnedDirectoryAnchor child;
                    if (Directory.Exists(childPath))
                    {
                        child = current.OpenExistingChild(segment);
                    }
                    else
                    {
                        await authorizeMutation();
                        using var creation = current.TryCreateChild(segment);
                        if (!creation.Created || !creation.VisiblePathMatches())
                        {
                            throw new MoveNeedsAttentionException(
                                "A quarantine child directory appeared before it could be claimed exclusively.");
                        }

                        child = creation.OpenCreatedDirectoryAnchor();
                    }

                    anchors.Add(child);
                    current = child;
                }

                var path = new PinnedCleanupDirectoryPath(anchors);
                path.EnsureVisibleHierarchy();
                return path;
            }
            catch
            {
                DisposeAnchors(anchors);
                throw;
            }
        }

        internal void EnsureVisibleHierarchy()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_anchors.Any(anchor => !anchor.VisiblePathMatches()))
            {
                throw new MoveNeedsAttentionException(
                    "A pinned source or quarantine directory changed during cleanup.");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            DisposeAnchors(_anchors);
            _disposed = true;
        }

        private static void DisposeAnchors(
            IReadOnlyList<PinnedDirectoryCreation.PinnedDirectoryAnchor> anchors)
        {
            for (var index = anchors.Count - 1; index >= 0; index--)
            {
                anchors[index].Dispose();
            }
        }
    }
}
