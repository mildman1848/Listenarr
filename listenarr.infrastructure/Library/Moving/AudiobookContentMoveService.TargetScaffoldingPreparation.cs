using System.Text.Json;
using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task<PreparedTargetScaffolding> PrepareScaffoldingAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string publishedRoot,
        string temporaryRoot,
        IReadOnlyList<MoveJobCreatedDirectory> ordered,
        CancellationToken cancellationToken)
    {
        if (File.Exists(temporaryRoot))
        {
            throw new MoveNeedsAttentionException(
                "The prepared target scaffold path is occupied by a file.");
        }

        var temporaryParent = Path.GetDirectoryName(temporaryRoot)
            ?? throw new MoveNeedsAttentionException(
                "The prepared target scaffold root has no parent directory.");
        var temporaryName = Path.GetFileName(temporaryRoot);
        PinnedDirectoryCreation publication;
        var createdByThisInvocation = !Directory.Exists(temporaryRoot);
        if (createdByThisInvocation)
        {
            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            publication = PinnedDirectoryCreation.TryCreateForPublication(
                temporaryParent,
                temporaryName);
            if (!publication.Created || !publication.VisiblePathMatches())
            {
                publication.Dispose();
                throw new MoveNeedsAttentionException(
                    "The prepared target scaffold root could not be claimed exclusively.");
            }
        }
        else
        {
            ValidateExistingMoveDirectory(temporaryRoot, "prepared target scaffolding");
            publication = PinnedDirectoryCreation.OpenExistingForPublication(
                temporaryParent,
                temporaryName);
        }

        PinnedDirectoryCreation.PinnedDirectoryAnchor? rootAnchor = null;
        PreparedTargetScaffolding? prepared = null;
        try
        {
            rootAnchor = publication.OpenCreatedDirectoryAnchor();
            if (!rootAnchor.VisiblePathMatches())
            {
                throw new MoveNeedsAttentionException(
                    "The prepared target scaffold root changed after it was pinned.");
            }

            var markerPath = Path.Join(temporaryRoot, ScaffoldOwnerFileName);
            if (createdByThisInvocation)
            {
                await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
                var marker = new ScaffoldOwnershipMarker(
                    ScaffoldMarkerVersion,
                    request.JobId,
                    target,
                    publishedRoot);
                await publication.WriteInsideFileAsync(
                    ScaffoldOwnerFileName,
                    JsonSerializer.Serialize(marker),
                    CancellationToken.None,
                    hiddenFile: false);
            }
            else if (!File.Exists(markerPath))
            {
                throw new MoveNeedsAttentionException(
                    "Existing prepared target scaffolding has no ownership marker and cannot be adopted safely.");
            }

            if (!rootAnchor.VisiblePathMatches())
            {
                throw new MoveNeedsAttentionException(
                    "The prepared target scaffold root changed during marker publication.");
            }
            ValidateScaffoldMarker(
                ReadScaffoldMarker(temporaryRoot),
                request.JobId,
                target,
                publishedRoot,
                request.TargetSemantics);
            if (!rootAnchor.VisiblePathMatches())
            {
                throw new MoveNeedsAttentionException(
                    "The prepared target scaffold root changed during marker validation.");
            }

            prepared = new PreparedTargetScaffolding(publication, rootAnchor);
            publication = null!;
            rootAnchor = null;
            var currentAnchor = prepared.RootAnchor;
            foreach (var directory in ordered.Skip(1))
            {
                if (!FileSystemPathIdentity.TryGetRelativePathWithinBase(
                        publishedRoot,
                        directory.Path,
                        request.TargetSemantics,
                        out var relativePath)
                    || !FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                        temporaryRoot,
                        relativePath,
                        request.TargetSemantics,
                        out var preparedPath))
                {
                    throw new MoveNeedsAttentionException(
                        "A target scaffold directory escaped the prepared scaffold root.");
                }

                var preparedParent = Path.GetDirectoryName(preparedPath);
                if (string.IsNullOrWhiteSpace(preparedParent)
                    || !FileSystemPathIdentity.AreEquivalent(
                        preparedParent,
                        currentAnchor.FullPath,
                        request.TargetSemantics))
                {
                    throw new MoveNeedsAttentionException(
                        "A prepared target scaffold directory is not a direct child of its pinned parent.");
                }
                if (File.Exists(preparedPath))
                {
                    throw new MoveNeedsAttentionException(
                        "A prepared target scaffold directory is occupied by a file.");
                }

                var childName = Path.GetFileName(preparedPath);
                PinnedDirectoryCreation.PinnedDirectoryAnchor childAnchor;
                if (Directory.Exists(preparedPath))
                {
                    ValidateExistingMoveDirectory(
                        preparedPath,
                        "prepared target scaffold directory");
                    childAnchor = currentAnchor.OpenExistingChild(childName);
                }
                else
                {
                    await EnsureMutationAuthorizedAsync(
                        request,
                        source,
                        target,
                        cancellationToken);
                    using var childCreation = currentAnchor.TryCreateChild(childName);
                    if (!childCreation.Created || !childCreation.VisiblePathMatches())
                    {
                        throw new MoveNeedsAttentionException(
                            "A prepared target scaffold child could not be claimed exclusively.");
                    }

                    childAnchor = childCreation.OpenCreatedDirectoryAnchor();
                }

                if (!childAnchor.VisiblePathMatches())
                {
                    childAnchor.Dispose();
                    throw new MoveNeedsAttentionException(
                        "A prepared target scaffold child changed after it was pinned.");
                }

                prepared.AddDescendant(childAnchor);
                currentAnchor = childAnchor;
            }

            prepared.EnsureVisibleHierarchy();
            ValidatePreparedScaffoldTree(
                temporaryRoot,
                publishedRoot,
                ordered,
                request.TargetSemantics);
            prepared.EnsureVisibleHierarchy();
            return prepared;
        }
        catch
        {
            prepared?.Dispose();
            rootAnchor?.Dispose();
            publication?.Dispose();
            throw;
        }
    }

    private sealed class PreparedTargetScaffolding : IDisposable
    {
        private readonly List<PinnedDirectoryCreation.PinnedDirectoryAnchor> _descendants = [];
        private bool _disposed;

        internal PreparedTargetScaffolding(
            PinnedDirectoryCreation publication,
            PinnedDirectoryCreation.PinnedDirectoryAnchor rootAnchor)
        {
            Publication = publication;
            RootAnchor = rootAnchor;
        }

        internal PinnedDirectoryCreation Publication { get; }

        internal PinnedDirectoryCreation.PinnedDirectoryAnchor RootAnchor { get; }

        internal void AddDescendant(
            PinnedDirectoryCreation.PinnedDirectoryAnchor descendant) =>
            _descendants.Add(descendant);

        internal void EnsureVisibleHierarchy()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!Publication.VisiblePathMatches()
                || !RootAnchor.VisiblePathMatches()
                || _descendants.Any(anchor => !anchor.VisiblePathMatches()))
            {
                throw new MoveNeedsAttentionException(
                    "The prepared target scaffold hierarchy changed while pinned.");
            }
        }

        internal PinnedDirectoryCreation.PinnedDirectoryAnchor PublishAs(string finalName)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureVisibleHierarchy();
            ReleaseDescendantAnchors();
            if (!Publication.VisiblePathMatches() || !RootAnchor.VisiblePathMatches())
            {
                throw new MoveNeedsAttentionException(
                    "The prepared target scaffold root changed before publication.");
            }

            return Publication.PublishCreatedDirectoryAs(finalName);
        }

        private void ReleaseDescendantAnchors()
        {
            for (var index = _descendants.Count - 1; index >= 0; index--)
            {
                _descendants[index].Dispose();
            }
            _descendants.Clear();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            ReleaseDescendantAnchors();
            RootAnchor.Dispose();
            Publication.Dispose();
            _disposed = true;
        }
    }
}
