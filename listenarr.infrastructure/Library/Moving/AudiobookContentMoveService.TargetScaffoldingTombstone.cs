using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private const string TargetScaffoldTemporaryArtifactType = "target-scaffold-temporary";
    private const string TargetScaffoldQuarantineArtifactType = "target-scaffold-quarantine";

    private async Task EnsureTargetScaffoldCleanupTombstoneAsync(
        string artifactRoot,
        string publishedRoot,
        AudiobookContentMoveRequest request,
        string artifactType,
        CancellationToken cancellationToken)
    {
        var tombstonePath = GetCleanupTombstonePath(
            artifactRoot,
            artifactType,
            request.JobId);
        var expectedTombstone = CreateTargetScaffoldCleanupTombstone(
            artifactRoot,
            publishedRoot,
            request,
            artifactType);
        await EnsureCleanupTombstoneAsync(
            tombstonePath,
            expectedTombstone,
            request.SourceSemantics,
            request.TargetSemantics,
            request.TargetSemantics,
            request.LeaseToken,
            () => EnsureMutationAuthorizedAsync(
                request,
                request.Source,
                request.Target,
                cancellationToken));
    }

    private async Task<bool> CleanupTargetScaffoldArtifactAsync(
        string artifactRoot,
        string publishedRoot,
        IReadOnlyCollection<MoveJobCreatedDirectory> scaffolding,
        AudiobookContentMoveRequest request,
        string artifactType,
        bool injectQuarantineDeleteFaults,
        CancellationToken cancellationToken)
    {
        var tombstonePath = GetCleanupTombstonePath(
            artifactRoot,
            artifactType,
            request.JobId);
        var expectedTombstone = CreateTargetScaffoldCleanupTombstone(
            artifactRoot,
            publishedRoot,
            request,
            artifactType);
        var hasTombstoneEvidence = HasCleanupTombstoneEvidence(tombstonePath);
        var artifactExists = IsSafeExistingScaffoldDirectory(
            artifactRoot,
            "target scaffold cleanup artifact");
        if (!hasTombstoneEvidence && !artifactExists)
        {
            return false;
        }

        if (!hasTombstoneEvidence)
        {
            ValidateTargetScaffoldArtifactTree(
                artifactRoot,
                publishedRoot,
                scaffolding,
                request,
                requireScaffoldMarker: true);
            await EnsureTargetScaffoldCleanupTombstoneAsync(
                artifactRoot,
                publishedRoot,
                request,
                artifactType,
                cancellationToken);
        }

        await ValidateTargetScaffoldCleanupTombstoneAsync(
            tombstonePath,
            expectedTombstone,
            request,
            cancellationToken);
        if (artifactExists)
        {
            ValidateTargetScaffoldArtifactTree(
                artifactRoot,
                publishedRoot,
                scaffolding,
                request,
                requireScaffoldMarker: false);
            await DeleteTargetScaffoldArtifactTreeAsync(
                artifactRoot,
                publishedRoot,
                scaffolding,
                request,
                injectQuarantineDeleteFaults,
                cancellationToken);
        }

        await DeleteTargetScaffoldCleanupTombstoneAsync(
            tombstonePath,
            expectedTombstone,
            request,
            cancellationToken);
        return true;
    }

    private MoveOwnershipMarker CreateTargetScaffoldCleanupTombstone(
        string artifactRoot,
        string publishedRoot,
        AudiobookContentMoveRequest request,
        string artifactType) =>
        CreateOwnershipMarker(
            CleanupTombstoneArtifactType,
            request.JobId,
            request.Source,
            request.Target,
            artifactRoot,
            artifactType,
            publishedRoot);

    private static bool IsSafeExistingScaffoldDirectory(
        string path,
        string description)
    {
        if (!TryGetExistingPathAttributes(path, out var attributes))
        {
            return false;
        }

        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                $"The {description} is a file, symbolic link, or reparse point.");
        }

        return true;
    }

    private static bool HasCleanupTombstoneEvidence(string markerPath)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(markerPath))
            ?? throw new MoveNeedsAttentionException(
                "The cleanup tombstone parent is unavailable.");
        ValidateExistingMoveDirectory(parent, "cleanup tombstone directory");
        return File.Exists(markerPath)
            || Directory.EnumerateFiles(
                parent,
                Path.GetFileName(markerPath) + ".writing-*",
                SearchOption.TopDirectoryOnly).Any();
    }

    private async Task ValidateTargetScaffoldCleanupTombstoneAsync(
        string tombstonePath,
        MoveOwnershipMarker expectedTombstone,
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureMutationAuthorizedAsync(
            request,
            request.Source,
            request.Target,
            cancellationToken);
        await RecoverOrReadOwnershipMarkerAsync(
            tombstonePath,
            expectedTombstone,
            request.SourceSemantics,
            request.TargetSemantics,
            request.TargetSemantics,
            request.LeaseToken,
            () => EnsureMutationAuthorizedAsync(
                request,
                request.Source,
                request.Target,
                cancellationToken));
    }

    private static void ValidateTargetScaffoldArtifactTree(
        string artifactRoot,
        string publishedRoot,
        IReadOnlyCollection<MoveJobCreatedDirectory> scaffolding,
        AudiobookContentMoveRequest request,
        bool requireScaffoldMarker)
    {
        ValidateExistingMoveDirectory(artifactRoot, "target scaffold cleanup artifact");
        var markerPath = Path.Join(artifactRoot, ScaffoldOwnerFileName);
        var hasScaffoldMarker = File.Exists(markerPath);
        if (hasScaffoldMarker)
        {
            ValidateScaffoldMarker(
                ReadScaffoldMarker(artifactRoot),
                request.JobId,
                request.Target,
                publishedRoot,
                request.TargetSemantics);
        }
        else if (requireScaffoldMarker)
        {
            throw new MoveNeedsAttentionException(
                "Target scaffolding cannot be cleaned because its ownership marker is missing.");
        }

        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                artifactRoot,
                out var files,
                out var directories,
                out var reason))
        {
            throw new MoveNeedsAttentionException(reason);
        }

        if (!hasScaffoldMarker && directories.Count > 0)
        {
            throw new MoveNeedsAttentionException(
                "Target scaffolding lost its ownership marker before nested cleanup completed.");
        }

        var unexpectedFiles = files.Where(file =>
            !FileSystemPathIdentity.AreEquivalent(
                file,
                markerPath,
                request.TargetSemantics)).ToList();
        if (unexpectedFiles.Count > 0)
        {
            throw new MoveNeedsAttentionException(
                "Target scaffold cleanup quarantine contains unexpected file content.");
        }

        var expectedDirectories = MapScaffoldDirectories(
            artifactRoot,
            publishedRoot,
            scaffolding);
        if (directories.Any(actual =>
            !expectedDirectories.Any(expected =>
                FileSystemPathIdentity.AreEquivalent(
                    actual,
                    expected,
                    request.TargetSemantics))))
        {
            throw new MoveNeedsAttentionException(
                "Target scaffold cleanup quarantine contains unexpected directory content.");
        }
    }

    private async Task DeleteTargetScaffoldArtifactTreeAsync(
        string artifactRoot,
        string publishedRoot,
        IReadOnlyCollection<MoveJobCreatedDirectory> scaffolding,
        AudiobookContentMoveRequest request,
        bool injectQuarantineDeleteFaults,
        CancellationToken cancellationToken)
    {
        foreach (var directory in MapScaffoldDirectories(
                artifactRoot,
                publishedRoot,
                scaffolding)
            .OrderByDescending(GetPathDepth))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            await RetirePinnedEmptyScaffoldDirectoryAsync(
                directory,
                () =>
                {
                    EnsurePublishedScaffoldNotRecreated(
                        publishedRoot,
                        artifactRoot,
                        injectQuarantineDeleteFaults);
                    if (Directory.EnumerateFileSystemEntries(directory).Any())
                    {
                        throw new MoveNeedsAttentionException(
                            "Target scaffold cleanup directory contains unexpected content.");
                    }
                },
                () => EnsureMutationAuthorizedAsync(
                    request,
                    request.Source,
                    request.Target,
                    cancellationToken),
                () => InjectTargetScaffoldDeleteFault(
                    request.JobId,
                    injectQuarantineDeleteFaults));
        }

        await EnsureMutationAuthorizedAsync(
            request,
            request.Source,
            request.Target,
            cancellationToken);
        EnsurePublishedScaffoldNotRecreated(
            publishedRoot,
            artifactRoot,
            injectQuarantineDeleteFaults);
        ValidateTargetScaffoldArtifactTree(
            artifactRoot,
            publishedRoot,
            scaffolding,
            request,
            requireScaffoldMarker: false);
        var markerPath = Path.Join(artifactRoot, ScaffoldOwnerFileName);
        if (File.Exists(markerPath))
        {
            await RetirePinnedArtifactAsync(
                markerPath,
                entry => ValidateScaffoldMarker(
                    ReadScaffoldMarker(entry),
                    request.JobId,
                    request.Target,
                    publishedRoot,
                    request.TargetSemantics),
                async () =>
                {
                    await EnsureMutationAuthorizedAsync(
                        request,
                        request.Source,
                        request.Target,
                        cancellationToken);
                    InjectTargetScaffoldDeleteFault(
                        request.JobId,
                        injectQuarantineDeleteFaults);
                });
        }

        await RetirePinnedEmptyScaffoldDirectoryAsync(
            artifactRoot,
            () =>
            {
                EnsurePublishedScaffoldNotRecreated(
                    publishedRoot,
                    artifactRoot,
                    injectQuarantineDeleteFaults);
                if (Directory.EnumerateFileSystemEntries(artifactRoot).Any())
                {
                    throw new MoveNeedsAttentionException(
                        "Target scaffold cleanup root contains unexpected content.");
                }
            },
            () => EnsureMutationAuthorizedAsync(
                request,
                request.Source,
                request.Target,
                cancellationToken),
            () => InjectTargetScaffoldDeleteFault(
                request.JobId,
                injectQuarantineDeleteFaults));
    }

    private async Task DeleteTargetScaffoldCleanupTombstoneAsync(
        string tombstonePath,
        MoveOwnershipMarker expectedTombstone,
        AudiobookContentMoveRequest request,
        CancellationToken cancellationToken)
    {
        await ValidateTargetScaffoldCleanupTombstoneAsync(
            tombstonePath,
            expectedTombstone,
            request,
            cancellationToken);
        var parent = Path.GetDirectoryName(Path.GetFullPath(tombstonePath))
            ?? throw new MoveNeedsAttentionException(
                "The target scaffold cleanup tombstone parent is unavailable.");
        ValidateExistingMoveDirectory(parent, "target scaffold cleanup tombstone directory");
        if (File.Exists(tombstonePath))
        {
            await RetirePinnedArtifactAsync(
                tombstonePath,
                entry =>
                {
                    var marker = ReadOwnershipMarker(entry, tombstonePath);
                    ValidateOwnershipMarker(
                        marker,
                        expectedTombstone,
                        request.SourceSemantics,
                        request.TargetSemantics,
                        request.TargetSemantics);
                },
                () => EnsureMutationAuthorizedAsync(
                    request,
                    request.Source,
                    request.Target,
                    cancellationToken));
        }
    }

    private static IReadOnlyList<string> MapScaffoldDirectories(
        string artifactRoot,
        string publishedRoot,
        IEnumerable<MoveJobCreatedDirectory> scaffolding) =>
        scaffolding
            .Skip(1)
            .Select(directory => Path.Join(
                artifactRoot,
                Path.GetRelativePath(publishedRoot, directory.Path)))
            .ToList();

    private static void EnsurePublishedScaffoldNotRecreated(
        string publishedRoot,
        string artifactRoot,
        bool required)
    {
        if (required
            && !string.Equals(
                Path.GetFullPath(publishedRoot),
                Path.GetFullPath(artifactRoot),
                StringComparison.Ordinal)
            && TryGetExistingPathAttributes(publishedRoot, out _))
        {
            throw new MoveNeedsAttentionException(
                "The published target scaffold was recreated during quarantine cleanup.");
        }
    }

    private void InjectTargetScaffoldDeleteFault(Guid jobId, bool enabled)
    {
        if (enabled)
        {
            faultInjector?.OnTargetScaffoldCleanup(
                jobId,
                TargetScaffoldCleanupFaultPoint.DuringQuarantineDelete);
        }
    }
}
