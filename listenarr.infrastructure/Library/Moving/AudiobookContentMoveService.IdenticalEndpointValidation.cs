namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    public async Task VerifyNoFilesystemMoveStartedAsync(
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var source = NormalizeMoveDirectoryEndpoint(request.Source);
        var target = NormalizeMoveDirectoryEndpoint(request.Target);
        await EnsureLeaseOwnedAsync(request.JobId, request.LeaseToken, cancellationToken);
        await ValidatePersistedMoveIdentityAsync(
            request.JobId,
            source,
            target,
            request.SourceSemantics,
            request.TargetSemantics,
            request.LeaseToken,
            cancellationToken);

        var manifest = await LoadManifestAsync(request.JobId, cancellationToken);
        var scaffolding = await GetCreatedDirectoriesAsync(request.JobId, cancellationToken);
        if (manifest.Count > 0 || scaffolding.Count > 0)
        {
            throw new MoveNeedsAttentionException(
                "The identical-endpoint job has durable move execution state and cannot be superseded automatically.");
        }

        var endpoints = new HashSet<string>(StringComparer.Ordinal)
        {
            source,
            target
        };
        foreach (var endpoint in endpoints)
        {
            VerifyEndpointContainsNoJobArtifacts(endpoint, request.JobId);
            VerifyEndpointParentContainsNoJobArtifacts(endpoint, request.JobId);
        }
    }

    private static void VerifyEndpointContainsNoJobArtifacts(
        string endpoint,
        Guid jobId)
    {
        if (!TryGetExistingPathAttributes(endpoint, out var attributes))
        {
            return;
        }

        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                "The identical move endpoint is a file, symbolic link, or reparse point.");
        }

        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                endpoint,
                out var files,
                out _,
                out var reason))
        {
            throw new MoveNeedsAttentionException(reason);
        }

        var markerName = $".listenarr-move-{jobId:N}.pending";
        var partialSuffix = $".listenarr-{jobId:N}.partial";
        if (files.Any(file =>
            string.Equals(Path.GetFileName(file), markerName, StringComparison.Ordinal)
            || Path.GetFileName(file).StartsWith(markerName + ".writing-", StringComparison.Ordinal)
            || file.EndsWith(partialSuffix, StringComparison.Ordinal)))
        {
            throw new MoveNeedsAttentionException(
                "The identical-endpoint job has move-owned filesystem artifacts and cannot be superseded automatically.");
        }
    }

    private static void VerifyEndpointParentContainsNoJobArtifacts(
        string endpoint,
        Guid jobId)
    {
        var parent = Path.GetDirectoryName(endpoint);
        if (string.IsNullOrWhiteSpace(parent))
        {
            return;
        }

        var tempDirectory = Path.Join(
            parent,
            Path.GetFileName(endpoint) + ".tmp-" + jobId.ToString("N"));
        var quarantine = Path.Join(parent, $".listenarr-quarantine-{jobId:N}");
        var possibleArtifacts = new[]
        {
            tempDirectory,
            quarantine,
            Path.Join(parent, $".listenarr-scaffold-{jobId:N}"),
            Path.Join(parent, $".listenarr-scaffold-cleanup-{jobId:N}"),
            GetCleanupDirectoryPath(tempDirectory, TemporaryDirectoryArtifactType, jobId),
            GetCleanupTombstonePath(tempDirectory, TemporaryDirectoryArtifactType, jobId),
            GetCleanupDirectoryPath(quarantine, QuarantineDirectoryArtifactType, jobId),
            GetCleanupTombstonePath(quarantine, QuarantineDirectoryArtifactType, jobId)
        };
        if (possibleArtifacts.Any(path => TryGetExistingPathAttributes(path, out _)))
        {
            throw new MoveNeedsAttentionException(
                "The identical-endpoint job has move-owned sibling artifacts and cannot be superseded automatically.");
        }
    }
}
