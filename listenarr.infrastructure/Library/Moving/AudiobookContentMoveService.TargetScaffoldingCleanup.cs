namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    public async Task RetainTargetScaffoldingAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        var scaffolding = (await GetCreatedDirectoriesAsync(request.JobId, cancellationToken))
            .OrderBy(directory => GetPathDepth(directory.Path))
            .ToList();
        if (scaffolding.Count == 0)
        {
            return;
        }

        var publishedRoot = scaffolding[0].Path;
        foreach (var directory in scaffolding.Where(directory =>
            directory.State is MoveCreatedDirectoryState.Created or MoveCreatedDirectoryState.Planned))
        {
            await UpdateCreatedDirectoryStateAsync(
                request.JobId,
                request.LeaseToken,
                directory.Path,
                MoveCreatedDirectoryState.Retained,
                cancellationToken);
        }

        if (!Directory.Exists(publishedRoot))
        {
            return;
        }

        var marker = ReadScaffoldMarker(publishedRoot);
        if (marker == null)
        {
            return;
        }

        ValidateScaffoldMarker(
            marker,
            request.JobId,
            request.Target,
            publishedRoot,
            request.TargetSemantics);
        await EnsureMutationAuthorizedAsync(
            request,
            request.Source,
            request.Target,
            cancellationToken);
        var markerPath = Path.Join(publishedRoot, ScaffoldOwnerFileName);
        if (File.Exists(markerPath))
        {
            File.Delete(markerPath);
        }
    }

    public async Task CleanupTerminalTargetScaffoldingAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        var scaffolding = (await GetCreatedDirectoriesAsync(request.JobId, cancellationToken))
            .OrderBy(directory => GetPathDepth(directory.Path))
            .ToList();
        if (scaffolding.Count == 0)
        {
            return;
        }

        var publishedRoot = scaffolding[0].Path;
        var parent = Path.GetDirectoryName(publishedRoot)
            ?? throw new MoveNeedsAttentionException(
                "The target scaffold root has no parent directory.");
        var temporaryRoot = GetTemporaryScaffoldRoot(parent, request.JobId);
        if (Directory.Exists(temporaryRoot))
        {
            ValidateScaffoldMarker(
                ReadScaffoldMarker(temporaryRoot),
                request.JobId,
                request.Target,
                publishedRoot,
                request.TargetSemantics);
            await EnsureMutationAuthorizedAsync(
                request,
                request.Source,
                request.Target,
                cancellationToken);
            DeleteOwnedScaffoldTree(temporaryRoot);
        }

        if (!Directory.Exists(publishedRoot))
        {
            foreach (var directory in scaffolding.Where(directory =>
                directory.State != MoveCreatedDirectoryState.Removed))
            {
                await UpdateCreatedDirectoryStateAsync(
                    request.JobId,
                    request.LeaseToken,
                    directory.Path,
                    MoveCreatedDirectoryState.Removed,
                    cancellationToken);
            }
            return;
        }

        var marker = ReadScaffoldMarker(publishedRoot);
        if (marker == null)
        {
            if (scaffolding.All(directory => directory.State == MoveCreatedDirectoryState.Retained))
            {
                return;
            }

            throw new MoveNeedsAttentionException(
                "Target scaffolding cannot be cleaned because its ownership marker is missing.");
        }

        ValidateScaffoldMarker(
            marker,
            request.JobId,
            request.Target,
            publishedRoot,
            request.TargetSemantics);
        if (!IsPublishedScaffoldEmpty(
                publishedRoot,
                scaffolding,
                request.Target,
                request.TargetSemantics))
        {
            foreach (var directory in scaffolding.Where(directory =>
                directory.State != MoveCreatedDirectoryState.Retained))
            {
                await UpdateCreatedDirectoryStateAsync(
                    request.JobId,
                    request.LeaseToken,
                    directory.Path,
                    MoveCreatedDirectoryState.Retained,
                    cancellationToken);
            }

            await EnsureMutationAuthorizedAsync(
                request,
                request.Source,
                request.Target,
                cancellationToken);
            var markerPath = Path.Join(publishedRoot, ScaffoldOwnerFileName);
            if (File.Exists(markerPath))
            {
                File.Delete(markerPath);
            }
            return;
        }

        var quarantine = Path.Join(parent, $".listenarr-scaffold-cleanup-{request.JobId:N}");
        if (Directory.Exists(quarantine) || File.Exists(quarantine))
        {
            throw new MoveNeedsAttentionException(
                "A prior target scaffold cleanup artifact already exists.");
        }

        await EnsureMutationAuthorizedAsync(
            request,
            request.Source,
            request.Target,
            cancellationToken);
        Directory.Move(publishedRoot, quarantine);
        DeleteOwnedScaffoldTree(quarantine);
        foreach (var directory in scaffolding.Where(directory =>
            directory.State != MoveCreatedDirectoryState.Removed))
        {
            await UpdateCreatedDirectoryStateAsync(
                request.JobId,
                request.LeaseToken,
                directory.Path,
                MoveCreatedDirectoryState.Removed,
                cancellationToken);
        }
    }
}
