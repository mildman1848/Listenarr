using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

public sealed class LibraryDirectoryOwnershipReconciler(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    LibraryDirectoryOwnershipBoundaryAuthorizer authorizer,
    IFilesystemMutationCoordinator mutationCoordinator,
    ILogger<LibraryDirectoryOwnershipReconciler> logger)
    : ILibraryDirectoryOwnershipReconciler
{
    public Task ReconcileAsync(CancellationToken cancellationToken = default) =>
        mutationCoordinator.ExecuteExclusiveAsync(
            ReconcileCoreAsync,
            cancellationToken);

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var retiredMarkers = await db.LibraryDirectoryOwnershipRetiredMarkers
            .Where(marker =>
                marker.State
                    == LibraryDirectoryOwnershipRetiredMarkerState.Pending)
            .ToListAsync(cancellationToken);
        foreach (var evidence in retiredMarkers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (string.IsNullOrWhiteSpace(evidence.CanonicalPayload)
                    || string.IsNullOrWhiteSpace(evidence.PayloadSha256)
                    || string.IsNullOrWhiteSpace(
                        evidence.CanonicalMarkerPath))
                {
                    LibraryDirectoryOwnershipRetiredMarkerEvidence
                        .MaterializeCanonicalPayload(evidence);
                    evidence.UpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                }

                ReconcileRetiredMarker(evidence);
                evidence.State =
                    LibraryDirectoryOwnershipRetiredMarkerState.Removed;
                evidence.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException or OutOfMemoryException
                    or StackOverflowException))
            {
                logger.LogWarning(
                    exception,
                    "Retired directory ownership marker evidence {EvidenceId} could not be reconciled safely.",
                    evidence.Id);
            }
        }

        var ownerships = await db.LibraryDirectoryOwnerships
            .Where(ownership =>
                ownership.State != LibraryDirectoryOwnershipState.Removed
                && !db.LibraryDirectoryOwnershipPathMigrations.Any(
                    migration => migration.OwnershipId == ownership.Id))
            .ToListAsync(cancellationToken);
        foreach (var ownership in ownerships)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ownership.State == LibraryDirectoryOwnershipState.Conflict)
            {
                continue;
            }

            try
            {
                if (ownership.State == LibraryDirectoryOwnershipState.Removing
                    && !Directory.Exists(ownership.CanonicalPath)
                    && !Directory.Exists(
                        LibraryDirectoryOwnershipRemoval.GetQuarantinePath(ownership)))
                {
                    using var missingAuthorization =
                        ownership.ManagedRootFolderId.HasValue
                            ? await authorizer.AuthorizeOwnershipAsync(
                                ownership,
                                cancellationToken)
                            : await authorizer.AuthorizeContainingRootAsync(
                                ownership.CanonicalPath,
                                ownership.GetIdentity().Semantics,
                                cancellationToken);
                    if (LibraryDirectoryOwnershipRemoval
                        .TryValidateLegacyMissingBothRecovery(
                            ownership,
                            missingAuthorization.ParentAnchor,
                            out var legacyPayload))
                    {
                        var now = DateTime.UtcNow;
                        db.LibraryDirectoryOwnershipRetiredMarkers.Add(
                            LibraryDirectoryOwnershipRetiredMarkerEvidence.Create(
                                ownership,
                                legacyPayload
                                    ?? throw new InvalidOperationException(
                                        "The validated legacy marker payload is unavailable."),
                                now));
                        ownership.State =
                            LibraryDirectoryOwnershipState.Removed;
                        ownership.PathOwnershipKey = null;
                        ownership.ManagedRootFolderId = null;
                        ownership.StateReason = null;
                        ownership.UpdatedAt = now;
                        await db.SaveChangesAsync(cancellationToken);
                        continue;
                    }

                    LibraryDirectoryOwnershipMarker.ValidateSiblingMarker(
                        ownership,
                        missingAuthorization.ParentAnchor);
                    continue;
                }

                using var authorization = ownership.ManagedRootFolderId.HasValue
                    ? await authorizer.AuthorizeOwnershipAsync(
                        ownership,
                        cancellationToken)
                    : await authorizer.AuthorizeContainingRootAsync(
                        ownership.CanonicalPath,
                        ownership.GetIdentity().Semantics,
                        cancellationToken);
                var directoryName = Path.GetFileName(ownership.CanonicalPath);
                var quarantineName =
                    $".listenarr-directory-removing-{ownership.OwnershipToken}";
                using var publication =
                    authorization.ParentAnchor.TryOpenExistingChildForPublication(
                        directoryName)
                    ?? (ownership.State == LibraryDirectoryOwnershipState.Removing
                        ? authorization.ParentAnchor.TryOpenExistingChildForPublication(
                            quarantineName)
                        : null)
                    ?? throw new InvalidOperationException(
                        "The owned directory and its recovery quarantine are missing.");
                using var directory = publication.OpenCreatedDirectoryAnchor();
                var liveIdentity = directory.GetDirectoryObjectIdentity();
                if (ownership.DirectoryObjectIdentityVersion.HasValue
                    && !string.Equals(
                        ownership.DirectoryObjectIdentity,
                        liveIdentity,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The live directory differs from its persisted physical identity.");
                }

                ownership.ManagedRootFolderId = authorization.RootFolderId;
                ownership.DirectoryObjectIdentityVersion = 1;
                ownership.DirectoryObjectIdentity = liveIdentity;
                ownership.DirectoryObjectIdentityUnavailableReason = null;
                ownership.StateReason = null;
                await PinnedLibraryDirectoryOwnershipMarker.ReconcileAsync(
                    ownership,
                    directory,
                    authorization.ParentAnchor,
                    cancellationToken);
                if (ownership.State == LibraryDirectoryOwnershipState.Unavailable)
                {
                    ownership.State = LibraryDirectoryOwnershipState.Owned;
                }
                ownership.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not (
                OperationCanceledException or OutOfMemoryException
                    or StackOverflowException))
            {
                ownership.DirectoryObjectIdentityUnavailableReason =
                    exception.Message;
                ownership.StateReason =
                    "Physical directory ownership could not be reconciled safely.";
                ownership.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(CancellationToken.None);
                logger.LogWarning(
                    exception,
                    "Directory ownership {OwnershipId} could not be reconciled and was disabled for destructive cleanup.",
                    ownership.Id);
            }
        }

    }

    private static void ReconcileRetiredMarker(
        LibraryDirectoryOwnershipRetiredMarker evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.CanonicalMarkerPath))
        {
            throw new InvalidOperationException(
                "The retired ownership marker path has not been materialized.");
        }

        var parentPath = Path.GetDirectoryName(evidence.CanonicalMarkerPath)
            ?? throw new InvalidOperationException(
                "The retired ownership marker has no parent directory.");
        using var parent =
            PinnedDirectoryCreation.OpenPinnedDirectoryNoFollow(parentPath);
        using var marker = parent.TryOpenExistingFile(
            Path.GetFileName(evidence.CanonicalMarkerPath),
            requireDeleteAccess: true);
        if (marker == null)
        {
            return;
        }

        var payload = LibraryDirectoryOwnershipMarker.ReadPayload(marker);
        if (!LibraryDirectoryOwnershipRetiredMarkerEvidence.Matches(
                evidence,
                payload)
            || !parent.VisiblePathMatches()
            || !marker.VisiblePathMatches()
            || !LibraryDirectoryOwnershipRetiredMarkerEvidence.Matches(
                evidence,
                LibraryDirectoryOwnershipMarker.ReadPayload(marker)))
        {
            throw new InvalidOperationException(
                "The retired ownership marker does not match its immutable cleanup evidence.");
        }

        marker.Delete();
    }
}
