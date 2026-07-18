using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private async Task<IReadOnlyList<MoveJobEntry>> LoadOrCreateManifestAsync(
        Guid jobId,
        MoveLeaseToken leaseToken,
        IReadOnlyList<ValidatedSourceEntry> validatedSourceEntries,
        CancellationToken cancellationToken)
    {
        var persisted = await LoadManifestAsync(jobId, cancellationToken);
        if (persisted.Count > 0)
        {
            return persisted;
        }

        var manifest = await BuildManifestAsync(
            jobId,
            validatedSourceEntries,
            cancellationToken,
            includeRootProofWhenEmpty: true);
        await PersistManifestAsync(jobId, leaseToken, manifest, cancellationToken);
        return manifest;
    }

    private async Task<List<MoveJobEntry>> SnapshotSourceAsync(
        Guid jobId,
        string source,
        string target,
        bool targetInsideSource,
        FileSystemPathSemantics sourceSemantics,
        CancellationToken cancellationToken,
        string? ownedRecoveryMarkerPath = null)
    {
        var scaffolding = await GetCreatedDirectoriesAsync(jobId, cancellationToken);
        var ownedSourceDirectories = await LoadValidatedOwnedSourceDirectoriesAsync(
            source,
            sourceSemantics,
            cancellationToken);
        var ownedSourceMarkerPaths = GetOwnedSourceMarkerPaths(
            source,
            ownedSourceDirectories,
            sourceSemantics);
        var validatedEntries = ValidateSourceTreeForMove(
            source,
            target,
            targetInsideSource,
            sourceSemantics,
            cancellationToken,
            ownedRecoveryMarkerPath,
            scaffolding.Select(directory => directory.Path).ToList(),
            ownedDirectoryMarkerPaths: ownedSourceMarkerPaths);
        return await BuildManifestAsync(jobId, validatedEntries, cancellationToken);
    }

    internal static void ValidateTargetManifest(
        string target,
        IReadOnlyCollection<MoveJobEntry> manifest,
        FileSystemPathSemantics targetSemantics)
    {
        var identities = new Dictionary<string, MoveJobEntry>(StringComparer.Ordinal);
        foreach (var entry in manifest)
        {
            if (IsRootManifestEntry(entry))
            {
                var rootKey = FileSystemPathIdentity.CreateKey(
                    "move-target",
                    target,
                    targetSemantics);
                if (identities.ContainsKey(rootKey))
                {
                    throw new MoveNeedsAttentionException(
                        "The manifest contains duplicate destination-root proof entries.");
                }

                identities.Add(rootKey, entry);
                continue;
            }

            if (Path.IsPathRooted(entry.RelativePath))
            {
                throw new MoveNeedsAttentionException(
                    "A manifest entry must be relative to the destination root.");
            }

            if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                target,
                entry.RelativePath,
                targetSemantics,
                out var destinationPath))
            {
                throw new MoveNeedsAttentionException(
                    "A manifest entry escaped the destination root.");
            }

            var key = FileSystemPathIdentity.CreateKey(
                "move-target",
                destinationPath,
                targetSemantics);
            if (identities.TryGetValue(key, out var existing))
            {
                throw new MoveNeedsAttentionException(
                    $"Target filesystem cannot represent both '{existing.RelativePath}' and '{entry.RelativePath}'.");
            }

            identities.Add(key, entry);
        }
    }

    private static async Task VerifyPublishedManifestAsync(
        string destinationRoot,
        IReadOnlyCollection<MoveJobEntry> manifest,
        FileSystemPathSemantics semantics,
        CancellationToken cancellationToken)
    {
        foreach (var entry in manifest)
        {
            if (IsRootManifestEntry(entry))
            {
                if (!Directory.Exists(destinationRoot))
                {
                    throw new MoveNeedsAttentionException(
                        "Published destination root is missing.");
                }

                continue;
            }

            if (!FileSystemPathIdentity.TryResolveRelativePathWithinBase(
                destinationRoot,
                entry.RelativePath,
                semantics,
                out var destinationPath))
            {
                throw new MoveNeedsAttentionException(
                    "A manifest entry escaped the destination root.");
            }

            if (entry.EntryType == MoveJobEntryType.Directory)
            {
                if (!Directory.Exists(destinationPath))
                {
                    throw new MoveNeedsAttentionException(
                        $"Published directory is missing: {entry.RelativePath}");
                }

                continue;
            }

            if (!File.Exists(destinationPath)
                || new FileInfo(destinationPath).Length != entry.Length
                || !string.Equals(
                    await ComputeSha256Async(destinationPath, cancellationToken),
                    entry.Sha256,
                    StringComparison.Ordinal))
            {
                throw new MoveNeedsAttentionException(
                    $"Published file verification failed: {entry.RelativePath}");
            }
        }
    }
}
