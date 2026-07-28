using Listenarr.Domain.Common;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    private const string QuarantineOwnershipMarkerFileName = ".listenarr-quarantine-owner.json";

    private sealed record ValidatedQuarantineOwnership(
        string DirectoryPath,
        string MarkerPath,
        MoveOwnershipMarker Marker);

    private async Task<ValidatedQuarantineOwnership> CreateOrValidateOwnedQuarantineDirectoryAsync(
        string quarantineRoot,
        string sourceParent,
        Guid jobId,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        MoveLeaseToken leaseToken,
        CancellationToken cancellationToken)
    {
        var markerPath = Path.Join(
            quarantineRoot,
            QuarantineOwnershipMarkerFileName);
        if (await TryCompleteOwnedDirectoryCleanupAsync(
                quarantineRoot,
                markerPath,
                QuarantineDirectoryArtifactType,
                jobId,
                source,
                target,
                sourceSemantics,
                targetSemantics,
                sourceSemantics,
                leaseToken,
                () => EnsureMutationAuthorizedAsync(
                    jobId,
                    leaseToken,
                    source,
                    target,
                    sourceSemantics,
                    targetSemantics,
                    cancellationToken)))
        {
            if (Directory.Exists(quarantineRoot) || File.Exists(quarantineRoot))
            {
                throw new MoveNeedsAttentionException(
                    "The original move quarantine path was recreated during cleanup and was preserved.");
            }

            // A prior completed cleanup left durable tombstone evidence.
        }
        else if (Directory.Exists(quarantineRoot))
        {
            try
            {
                return await ValidateOwnedQuarantineDirectoryAsync(
                    quarantineRoot,
                    sourceParent,
                    jobId,
                    source,
                    target,
                    sourceSemantics,
                    targetSemantics,
                    leaseToken,
                    cancellationToken);
            }
            catch (InterruptedOwnershipPublicationException)
            {
                await RetirePinnedEmptyDirectoryAsync(
                    quarantineRoot,
                    "interrupted quarantine directory",
                    () =>
                    {
                        ValidateExistingMoveDirectory(
                            quarantineRoot,
                            "interrupted quarantine directory");
                        if (Directory.EnumerateFileSystemEntries(quarantineRoot).Any())
                        {
                            throw new MoveNeedsAttentionException(
                                "An interrupted quarantine ownership publication left unexpected content.");
                        }
                    },
                    () => EnsureMutationAuthorizedAsync(
                        jobId,
                        leaseToken,
                        source,
                        target,
                        sourceSemantics,
                        targetSemantics,
                        cancellationToken));
            }
        }

        if (File.Exists(quarantineRoot))
        {
            throw new MoveNeedsAttentionException(
                "The move quarantine path is occupied by a file and cannot be claimed safely.");
        }

        ValidateMoveRootPath(quarantineRoot, mustExist: false, "quarantine");
        var normalizedSourceParent = Path.GetFullPath(sourceParent);
        var normalizedQuarantineRoot = Path.GetFullPath(quarantineRoot);
        var quarantineParent = Path.GetDirectoryName(normalizedQuarantineRoot);
        if (string.IsNullOrWhiteSpace(quarantineParent)
            || !FileSystemPathIdentity.AreEquivalent(
                normalizedSourceParent,
                quarantineParent,
                sourceSemantics))
        {
            throw new MoveNeedsAttentionException(
                "The move quarantine directory escaped its validated source parent.");
        }

        await EnsureMutationAuthorizedAsync(
            jobId,
            leaseToken,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            cancellationToken);
        ValidateMoveRootPath(quarantineRoot, mustExist: false, "quarantine");
        using var quarantineCreation = PinnedDirectoryCreation.TryCreate(
            normalizedSourceParent,
            Path.GetFileName(normalizedQuarantineRoot));
        if (!quarantineCreation.Created)
        {
            throw new MoveNeedsAttentionException(
                "The move quarantine directory appeared before Listenarr could claim it exclusively.");
        }
        if (!quarantineCreation.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The move quarantine parent changed during exclusive creation.");
        }

        using var quarantineAnchor = quarantineCreation.OpenCreatedDirectoryAnchor();
        if (!quarantineAnchor.VisiblePathMatches())
        {
            throw new MoveNeedsAttentionException(
                "The move quarantine directory identity changed after exclusive creation.");
        }

        ValidateExistingMoveDirectory(quarantineRoot, "quarantine directory");
        var marker = CreateOwnershipMarker(
            QuarantineDirectoryArtifactType,
            jobId,
            source,
            target,
            quarantineRoot);
        try
        {
            await EnsureMutationAuthorizedAsync(
                jobId,
                leaseToken,
                source,
                target,
                sourceSemantics,
                targetSemantics,
                cancellationToken);
            await PublishOwnershipMarkerAsync(
                markerPath,
                marker,
                OwnershipMarkerKind.QuarantineDirectory,
                leaseToken,
                () => EnsureMutationAuthorizedAsync(
                    jobId,
                    leaseToken,
                    source,
                    target,
                    sourceSemantics,
                    targetSemantics,
                    cancellationToken),
                quarantineAnchor);
            return await ValidateOwnedQuarantineDirectoryAsync(
                quarantineRoot,
                sourceParent,
                jobId,
                source,
                target,
                sourceSemantics,
                targetSemantics,
                leaseToken,
                cancellationToken);
        }
        catch (Exception exception) when (exception is MoveLeaseLostException or PersistenceException)
        {
            throw;
        }
        catch (MoveNeedsAttentionException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await TryRemoveNewEmptyOwnershipDirectoryAsync(
                quarantineRoot,
                jobId,
                "quarantine",
                () => EnsureMutationAuthorizedAsync(
                    jobId,
                    leaseToken,
                    source,
                    target,
                    sourceSemantics,
                    targetSemantics,
                    cancellationToken));
            throw;
        }
        catch (Exception exception) when (WorkerExceptionClassifier.IsNonFatal(exception))
        {
            await TryRemoveNewEmptyOwnershipDirectoryAsync(
                quarantineRoot,
                jobId,
                "quarantine",
                () => EnsureMutationAuthorizedAsync(
                    jobId,
                    leaseToken,
                    source,
                    target,
                    sourceSemantics,
                    targetSemantics,
                    cancellationToken));
            throw new MoveNeedsAttentionException(
                $"The move quarantine directory could not be claimed safely: {exception.Message}");
        }
    }

    private async Task<ValidatedQuarantineOwnership> ValidateOwnedQuarantineDirectoryAsync(
        string quarantineRoot,
        string sourceParent,
        Guid jobId,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        MoveLeaseToken leaseToken,
        CancellationToken cancellationToken)
    {
        if (!FileSystemSafety.TryValidateMutationTarget(
                quarantineRoot,
                [sourceParent],
                out var safeQuarantineRoot,
                out var quarantineReason))
        {
            throw new MoveNeedsAttentionException(quarantineReason);
        }

        ValidateExistingMoveDirectory(
            safeQuarantineRoot,
            "quarantine directory");
        var markerPath = Path.Join(
            safeQuarantineRoot,
            QuarantineOwnershipMarkerFileName);
        var expectedMarker = CreateOwnershipMarker(
            QuarantineDirectoryArtifactType,
            jobId,
            source,
            target,
            quarantineRoot);
        var marker = await RecoverOrReadOwnershipMarkerAsync(
            markerPath,
            expectedMarker,
            sourceSemantics,
            targetSemantics,
            sourceSemantics,
            leaseToken,
            () => EnsureMutationAuthorizedAsync(
                jobId,
                leaseToken,
                source,
                target,
                sourceSemantics,
                targetSemantics,
                cancellationToken));

        var ownership = new ValidatedQuarantineOwnership(
            safeQuarantineRoot,
            markerPath,
            marker);
        ValidateOwnedQuarantineTree(ownership);
        return ownership;
    }

    private async Task<ValidatedQuarantineOwnership?> TryValidateExistingQuarantineDirectoryAsync(
        string source,
        string target,
        Guid jobId,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        MoveLeaseToken leaseToken,
        CancellationToken cancellationToken)
    {
        var sourceParent = Path.GetDirectoryName(Path.GetFullPath(source))
            ?? throw new MoveNeedsAttentionException("The source parent is unavailable.");
        var quarantineRoot = Path.Join(
            sourceParent,
            $".listenarr-quarantine-{jobId:N}");
        var markerPath = Path.Join(
            quarantineRoot,
            QuarantineOwnershipMarkerFileName);
        if (await TryCompleteOwnedDirectoryCleanupAsync(
                quarantineRoot,
                markerPath,
                QuarantineDirectoryArtifactType,
                jobId,
                source,
                target,
                sourceSemantics,
                targetSemantics,
                sourceSemantics,
                leaseToken,
                () => EnsureMutationAuthorizedAsync(
                    jobId,
                    leaseToken,
                    source,
                    target,
                    sourceSemantics,
                    targetSemantics,
                    cancellationToken)))
        {
            return null;
        }

        if (!Directory.Exists(quarantineRoot))
        {
            if (File.Exists(quarantineRoot))
            {
                throw new MoveNeedsAttentionException(
                    "The move quarantine path is occupied by a file and cannot be validated safely.");
            }

            return null;
        }

        return await ValidateOwnedQuarantineDirectoryAsync(
            quarantineRoot,
            sourceParent,
            jobId,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            leaseToken,
            cancellationToken);
    }

    private static void ValidateOwnedQuarantineTree(
        ValidatedQuarantineOwnership ownership)
    {
        ValidateExistingMoveDirectory(
            ownership.DirectoryPath,
            "quarantine directory");
        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                ownership.DirectoryPath,
                out _,
                out _,
                out var reason))
        {
            throw new MoveNeedsAttentionException(
                $"The move quarantine directory could not be traversed safely: {reason}");
        }
    }

    private static void ValidateQuarantineMutationPath(
        ValidatedQuarantineOwnership ownership,
        string path)
    {
        ValidateOwnedQuarantineTree(ownership);
        if (!FileSystemSafety.TryValidateMutationTarget(
                path,
                [ownership.DirectoryPath],
                out path,
                out var reason))
        {
            throw new MoveNeedsAttentionException(reason);
        }

        if ((File.Exists(path) || Directory.Exists(path))
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new MoveNeedsAttentionException(
                "A move quarantine entry is a symbolic link or reparse point.");
        }
    }

    private async Task DeleteEmptyOwnedQuarantineDirectoryAsync(
        ValidatedQuarantineOwnership ownership,
        Guid jobId,
        string source,
        string target,
        FileSystemPathSemantics sourceSemantics,
        FileSystemPathSemantics targetSemantics,
        MoveLeaseToken leaseToken,
        CancellationToken cancellationToken)
    {
        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                ownership.DirectoryPath,
                out var files,
                out var directories,
                out var reason))
        {
            throw new MoveNeedsAttentionException(
                $"The completed move quarantine could not be traversed safely: {reason}");
        }

        var unexpectedFile = files.FirstOrDefault(file =>
            !FileSystemPathIdentity.AreEquivalent(
                file,
                ownership.MarkerPath,
                sourceSemantics));
        if (unexpectedFile != null)
        {
            throw new MoveNeedsAttentionException(
                $"The completed move quarantine contains an unexpected file: {Path.GetFileName(unexpectedFile)}");
        }

        if (directories.Any(directory =>
                Directory.EnumerateFileSystemEntries(directory).Any()))
        {
            throw new MoveNeedsAttentionException(
                "The completed move quarantine contains an unexpected non-empty directory.");
        }

        await DeleteOwnedDirectoryWithTombstoneAsync(
            ownership.DirectoryPath,
            ownership.MarkerPath,
            QuarantineDirectoryArtifactType,
            jobId,
            source,
            target,
            sourceSemantics,
            targetSemantics,
            sourceSemantics,
            leaseToken,
            () => EnsureMutationAuthorizedAsync(
                jobId,
                leaseToken,
                source,
                target,
                sourceSemantics,
                targetSemantics,
                cancellationToken));
    }
}
