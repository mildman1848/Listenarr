namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private sealed class PinnedMoveDirectoryPath : IDisposable
    {
        private readonly List<PinnedDirectoryCreation.PinnedDirectoryAnchor> _anchors;
        private bool _disposed;

        private PinnedMoveDirectoryPath(
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

        internal static PinnedMoveDirectoryPath OpenExisting(
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

                var path = new PinnedMoveDirectoryPath(anchors);
                path.EnsureVisibleHierarchy();
                return path;
            }
            catch
            {
                DisposeAnchors(anchors);
                throw;
            }
        }

        internal static async Task<PinnedMoveDirectoryPath> OpenOrCreateAsync(
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
                                "A move-owned child directory appeared before it could be claimed exclusively.");
                        }

                        child = creation.OpenCreatedDirectoryAnchor();
                    }

                    anchors.Add(child);
                    current = child;
                }

                var path = new PinnedMoveDirectoryPath(anchors);
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
                    "A pinned move directory hierarchy changed during mutation.");
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
