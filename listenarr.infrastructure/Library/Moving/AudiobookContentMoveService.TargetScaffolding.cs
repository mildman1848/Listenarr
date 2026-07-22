using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private const string ScaffoldOwnerFileName = ".listenarr-scaffold-owner.json";
    private const int ScaffoldMarkerVersion = 1;
    private const long MaximumScaffoldMarkerBytes = 64 * 1024;

    private async Task<IReadOnlyList<MoveJobCreatedDirectory>> PlanTargetScaffoldingAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        string targetParent,
        CancellationToken cancellationToken)
    {
        var persisted = await GetCreatedDirectoriesAsync(request.JobId, cancellationToken);
        if (persisted.Count == 0)
        {
            var missing = FindMissingTargetAncestors(targetParent);
            await PersistCreatedDirectoriesAsync(
                request.JobId,
                request.LeaseToken,
                missing,
                cancellationToken);
            persisted = await GetCreatedDirectoriesAsync(request.JobId, cancellationToken);
        }
        else if (persisted.All(directory =>
            !Directory.Exists(directory.Path) && !File.Exists(directory.Path)))
        {
            // Terminal cleanup can leave the durable ledger in Removed state. A manual
            // retry of the same job must reacquire those absent paths before recreating
            // them, otherwise a successful retry records live directories as Removed.
            foreach (var directory in persisted.Where(directory =>
                directory.State != MoveCreatedDirectoryState.Planned))
            {
                await UpdateCreatedDirectoryStateAsync(
                    request.JobId,
                    request.LeaseToken,
                    directory.Path,
                    MoveCreatedDirectoryState.Planned,
                    cancellationToken);
            }

            persisted = await GetCreatedDirectoriesAsync(request.JobId, cancellationToken);
        }

        foreach (var directory in persisted)
        {
            ValidateScaffoldIdentity(directory.Path, target, request.TargetSemantics);
        }

        return persisted;
    }

    private async Task CreateOrValidateTargetScaffoldingAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        IReadOnlyList<MoveJobCreatedDirectory> scaffolding,
        CancellationToken cancellationToken)
    {
        if (scaffolding.Count == 0)
        {
            return;
        }

        var ordered = scaffolding
            .OrderBy(directory => GetPathDepth(directory.Path))
            .ToList();
        foreach (var directory in ordered)
        {
            ValidateScaffoldIdentity(directory.Path, target, request.TargetSemantics);
        }

        var publishedRoot = ordered[0].Path;
        var parent = Path.GetDirectoryName(publishedRoot)
            ?? throw new MoveNeedsAttentionException(
                "The target scaffold root has no parent directory.");
        ValidateExistingMoveDirectory(parent, "target scaffold parent");
        var temporaryRoot = GetTemporaryScaffoldRoot(parent, request.JobId);
        if (Directory.Exists(publishedRoot))
        {
            if (Directory.Exists(temporaryRoot) || File.Exists(temporaryRoot))
            {
                throw new MoveNeedsAttentionException(
                    "Both prepared and published target scaffolding exist.");
            }

            await AdoptOrValidatePublishedScaffoldingAsync(
                request,
                target,
                publishedRoot,
                ordered,
                cancellationToken);
            return;
        }

        if (File.Exists(publishedRoot))
        {
            throw new MoveNeedsAttentionException(
                "The planned target scaffold root is occupied by a file.");
        }

        await PrepareScaffoldingAsync(
            request,
            source,
            target,
            publishedRoot,
            temporaryRoot,
            ordered,
            cancellationToken);
        await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
        if (Directory.Exists(publishedRoot) || File.Exists(publishedRoot))
        {
            throw new MoveNeedsAttentionException(
                "The target scaffold root appeared before Listenarr could publish its owned scaffolding.");
        }

        ValidateExistingMoveDirectory(temporaryRoot, "prepared target scaffolding");
        ValidateScaffoldMarker(
            ReadScaffoldMarker(temporaryRoot),
            request.JobId,
            target,
            publishedRoot,
            request.TargetSemantics);
        PublishTargetScaffoldingForTestableBoundary(request.JobId, temporaryRoot, publishedRoot);
        ValidateExistingMoveDirectory(publishedRoot, "published target scaffolding");
        ValidatePublishedScaffoldTree(
            publishedRoot,
            ordered,
            target,
            request.TargetSemantics,
            requireMarker: true);
        foreach (var directory in ordered.Where(directory =>
            directory.State == MoveCreatedDirectoryState.Planned))
        {
            await UpdateCreatedDirectoryStateAsync(
                request.JobId,
                request.LeaseToken,
                directory.Path,
                MoveCreatedDirectoryState.Created,
                cancellationToken);
        }
    }

    private async Task PrepareScaffoldingAsync(
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

        if (!Directory.Exists(temporaryRoot))
        {
            await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
            Directory.CreateDirectory(temporaryRoot);
            ValidateExistingMoveDirectory(temporaryRoot, "prepared target scaffolding");
            WriteScaffoldMarker(
                temporaryRoot,
                new ScaffoldOwnershipMarker(
                    ScaffoldMarkerVersion,
                    request.JobId,
                    target,
                    publishedRoot));
        }
        else
        {
            ValidateExistingMoveDirectory(temporaryRoot, "prepared target scaffolding");
            ValidateScaffoldMarker(
                ReadScaffoldMarker(temporaryRoot),
                request.JobId,
                target,
                publishedRoot,
                request.TargetSemantics);
        }

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

            if (File.Exists(preparedPath))
            {
                throw new MoveNeedsAttentionException(
                    "A prepared target scaffold directory is occupied by a file.");
            }

            if (!Directory.Exists(preparedPath))
            {
                await EnsureMutationAuthorizedAsync(request, source, target, cancellationToken);
                Directory.CreateDirectory(preparedPath);
            }

            ValidateExistingMoveDirectory(preparedPath, "prepared target scaffold directory");
        }

        ValidatePreparedScaffoldTree(
            temporaryRoot,
            publishedRoot,
            ordered,
            request.TargetSemantics);
    }

    private async Task AdoptOrValidatePublishedScaffoldingAsync(
        AudiobookContentMoveRequest request,
        string target,
        string publishedRoot,
        IReadOnlyList<MoveJobCreatedDirectory> ordered,
        CancellationToken cancellationToken)
    {
        ValidateExistingMoveDirectory(publishedRoot, "published target scaffolding");
        var marker = ReadScaffoldMarker(publishedRoot);
        if (marker == null)
        {
            if (ordered.All(directory => directory.State == MoveCreatedDirectoryState.Retained))
            {
                ValidatePublishedScaffoldTree(
                    publishedRoot,
                    ordered,
                    target,
                    request.TargetSemantics,
                    requireMarker: false);
                return;
            }

            foreach (var directory in ordered.Where(directory =>
                directory.State == MoveCreatedDirectoryState.Planned))
            {
                await UpdateCreatedDirectoryStateAsync(
                    request.JobId,
                    request.LeaseToken,
                    directory.Path,
                    MoveCreatedDirectoryState.Retained,
                    cancellationToken);
            }

            throw new MoveNeedsAttentionException(
                "Published target scaffolding exists without its ownership marker and cannot be adopted safely.");
        }

        ValidateScaffoldMarker(
            marker,
            request.JobId,
            target,
            publishedRoot,
            request.TargetSemantics);
        ValidatePublishedScaffoldTree(
            publishedRoot,
            ordered,
            target,
            request.TargetSemantics,
            requireMarker: true);
        foreach (var directory in ordered.Where(directory =>
            directory.State == MoveCreatedDirectoryState.Planned))
        {
            await UpdateCreatedDirectoryStateAsync(
                request.JobId,
                request.LeaseToken,
                directory.Path,
                MoveCreatedDirectoryState.Created,
                cancellationToken);
        }
    }

    internal static IReadOnlyList<string> GetTargetStructuralSpine(
        string source,
        string target,
        FileSystemPathSemantics semantics)
    {
        if (!FileSystemPathIdentity.IsSameOrInside(target, source, semantics)
            || FileSystemPathIdentity.AreEquivalent(target, source, semantics))
        {
            return [];
        }

        var result = new Stack<string>();
        var current = Path.GetDirectoryName(target);
        while (!string.IsNullOrWhiteSpace(current)
            && !FileSystemPathIdentity.AreEquivalent(current, source, semantics))
        {
            result.Push(current);
            current = Path.GetDirectoryName(current);
        }

        if (string.IsNullOrWhiteSpace(current))
        {
            throw new MoveNeedsAttentionException(
                "The nested move target does not share the expected source boundary.");
        }

        return result.ToList();
    }

    internal static void ValidateExistingTargetSpine(
        IReadOnlyList<string> spine,
        string target,
        FileSystemPathSemantics semantics)
    {
        for (var index = 0; index < spine.Count; index++)
        {
            var directory = spine[index];
            if (!Directory.Exists(directory))
            {
                break;
            }

            ValidateExistingMoveDirectory(directory, "nested target structural directory");
            var expectedChild = index + 1 < spine.Count ? spine[index + 1] : target;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (!FileSystemPathIdentity.AreEquivalent(entry, expectedChild, semantics))
                {
                    throw new MoveNeedsAttentionException(
                        "A nested target structural directory contains unexpected content unrelated to the target path.");
                }
            }
        }
    }

    private static IReadOnlyList<string> FindMissingTargetAncestors(string targetParent)
    {
        var missing = new Stack<string>();
        var current = Path.GetFullPath(targetParent);
        while (!Directory.Exists(current))
        {
            if (File.Exists(current))
            {
                throw new MoveNeedsAttentionException(
                    "A target ancestor is occupied by a file.");
            }

            missing.Push(current);
            current = Path.GetDirectoryName(current)
                ?? throw new MoveNeedsAttentionException(
                    "No existing ancestor could be found for the move target.");
        }

        ValidateExistingMoveDirectory(current, "nearest existing target ancestor");
        return missing.ToList();
    }

    private static void ValidateScaffoldIdentity(
        string scaffold,
        string target,
        FileSystemPathSemantics semantics)
    {
        try
        {
            if (FileSystemPathIdentity.AreEquivalent(scaffold, target, semantics)
                || !FileSystemPathIdentity.IsSameOrInside(target, scaffold, semantics))
            {
                throw new MoveNeedsAttentionException(
                    "A persisted move-created directory is not an ancestor of the target.");
            }
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is
            ArgumentException or InvalidOperationException or NotSupportedException or PathTooLongException)
        {
            throw new MoveNeedsAttentionException(
                "A persisted move-created directory has an invalid path identity.");
        }
    }

    private static void ValidatePreparedScaffoldTree(
        string temporaryRoot,
        string publishedRoot,
        IReadOnlyList<MoveJobCreatedDirectory> ordered,
        FileSystemPathSemantics semantics)
    {
        for (var index = 0; index < ordered.Count; index++)
        {
            var finalPath = ordered[index].Path;
            var actualPath = index == 0
                ? temporaryRoot
                : ResolveScaffoldPath(temporaryRoot, publishedRoot, finalPath, semantics);
            ValidateExistingMoveDirectory(actualPath, "prepared target scaffold directory");
            var allowedChild = index + 1 < ordered.Count
                ? ResolveScaffoldPath(
                    temporaryRoot,
                    publishedRoot,
                    ordered[index + 1].Path,
                    semantics)
                : null;
            foreach (var entry in Directory.EnumerateFileSystemEntries(actualPath))
            {
                if (index == 0
                    && string.Equals(
                        Path.GetFileName(entry),
                        ScaffoldOwnerFileName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (allowedChild == null
                    || !FileSystemPathIdentity.AreEquivalent(entry, allowedChild, semantics))
                {
                    throw new MoveNeedsAttentionException(
                        "Prepared target scaffolding contains unexpected content.");
                }
            }
        }
    }

    private static void ValidatePublishedScaffoldTree(
        string publishedRoot,
        IReadOnlyList<MoveJobCreatedDirectory> ordered,
        string target,
        FileSystemPathSemantics semantics,
        bool requireMarker)
    {
        for (var index = 0; index < ordered.Count; index++)
        {
            var directory = ordered[index].Path;
            ValidateExistingMoveDirectory(directory, "published target scaffold directory");
            var expectedChild = index + 1 < ordered.Count ? ordered[index + 1].Path : target;
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (index == 0
                    && string.Equals(
                        Path.GetFileName(entry),
                        ScaffoldOwnerFileName,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (!FileSystemPathIdentity.AreEquivalent(entry, expectedChild, semantics))
                {
                    throw new MoveNeedsAttentionException(
                        "Published target scaffolding contains unexpected content.");
                }
            }
        }

        var markerExists = File.Exists(Path.Join(publishedRoot, ScaffoldOwnerFileName));
        if (requireMarker != markerExists)
        {
            throw new MoveNeedsAttentionException(
                requireMarker
                    ? "Published target scaffolding has no ownership marker."
                    : "Retained target scaffolding still has an ownership marker.");
        }
    }

    private static bool IsPublishedScaffoldEmpty(
        string publishedRoot,
        IReadOnlyList<MoveJobCreatedDirectory> ordered,
        string target,
        FileSystemPathSemantics semantics)
    {
        if (Directory.Exists(target) || File.Exists(target))
        {
            return false;
        }

        try
        {
            ValidatePublishedScaffoldTree(
                publishedRoot,
                ordered,
                target,
                semantics,
                requireMarker: true);
            return true;
        }
        catch (MoveNeedsAttentionException)
        {
            return false;
        }
    }

}
