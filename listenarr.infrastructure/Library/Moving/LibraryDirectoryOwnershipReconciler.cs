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
        var ownerships = await db.LibraryDirectoryOwnerships
            .Where(ownership => ownership.State != LibraryDirectoryOwnershipState.Removed)
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
                    LibraryDirectoryOwnershipRemoval.ValidateRecoverableState(ownership);
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
                if (ownership.State == LibraryDirectoryOwnershipState.Unavailable)
                {
                    ownership.State = LibraryDirectoryOwnershipState.Owned;
                }
                ownership.StateReason = null;
                await PinnedLibraryDirectoryOwnershipMarker.UpgradeLegacyAsync(
                    ownership,
                    directory,
                    authorization.ParentAnchor,
                    cancellationToken);
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
}
