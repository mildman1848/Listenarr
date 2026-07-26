using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

public sealed class RootFolderObjectIdentityReconciler(
    IDbContextFactory<ListenArrDbContext> dbContextFactory,
    IDirectoryObjectIdentityResolver identityResolver,
    IFilesystemMutationCoordinator mutationCoordinator,
    ILogger<RootFolderObjectIdentityReconciler> logger)
    : IRootFolderObjectIdentityReconciler
{
    public Task ReconcileAsync(CancellationToken cancellationToken = default) =>
        mutationCoordinator.ExecuteExclusiveAsync(
            ReconcileCoreAsync,
            cancellationToken);

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var roots = await db.RootFolders.ToListAsync(cancellationToken);
        foreach (var root in roots)
        {
            var current = await identityResolver.ResolveAsync(root.Path, cancellationToken);
            if (root.DirectoryObjectIdentityVersion == null
                || string.IsNullOrWhiteSpace(root.DirectoryObjectIdentity))
            {
                root.DirectoryObjectIdentityVersion = current.Version;
                root.DirectoryObjectIdentity = current.Value;
                root.DirectoryObjectIdentityUnavailableReason = current.UnavailableReason;
                continue;
            }

            if (!current.IsAvailable
                || current.Version != root.DirectoryObjectIdentityVersion
                || !string.Equals(
                    current.Value,
                    root.DirectoryObjectIdentity,
                    StringComparison.Ordinal))
            {
                root.DirectoryObjectIdentityUnavailableReason =
                    current.UnavailableReason
                    ?? "The live directory no longer matches its enrolled physical identity.";
                logger.LogWarning(
                    "Root folder {RootFolderId} physical identity is unavailable or mismatched; destructive ownership cleanup is disabled.",
                    root.Id);
                continue;
            }

            root.DirectoryObjectIdentityUnavailableReason = null;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
