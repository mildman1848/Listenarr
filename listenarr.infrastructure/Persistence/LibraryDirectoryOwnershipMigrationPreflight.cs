using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

internal static class LibraryDirectoryOwnershipMigrationPreflight
{
    internal const string PredecessorMigrationId =
        "20260726042801_AddDirectoryObjectIdentityAuthorization";
    internal const string ForeignKeyMigrationId =
        "20260726500000_AddLibraryDirectoryOwnershipRootForeignKey";

    public static int RepairLegacyForeignKeyReferences(ListenArrDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var applied = context.Database.GetAppliedMigrations()
            .ToHashSet(StringComparer.Ordinal);
        if (!applied.Contains(PredecessorMigrationId)
            || applied.Contains(ForeignKeyMigrationId))
        {
            return 0;
        }

        using var transaction = context.Database.BeginTransaction();

        // Removed legacy rows no longer own their directory. Clear only a stale
        // reference to a root that no longer exists so the upcoming FK rebuild
        // can copy the row. Post-migration reconciliation materializes durable
        // retired-marker evidence before attempting any marker cleanup.
        var removedRows = context.Database.ExecuteSqlRaw(
            """
            UPDATE "LibraryDirectoryOwnerships"
            SET "ManagedRootFolderId" = NULL
            WHERE "State" = 'Removed'
              AND "ManagedRootFolderId" IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM "RootFolders" AS "root"
                  WHERE "root"."Id" =
                      "LibraryDirectoryOwnerships"."ManagedRootFolderId");
            """);

        // A live ownership pointing at a deleted root must fail closed. This is
        // a data-integrity repair only; it grants no filesystem authority and
        // deliberately removes the ownership key before the FK is introduced.
        var unavailableRows = context.Database.ExecuteSqlRaw(
            """
            UPDATE "LibraryDirectoryOwnerships"
            SET
                "State" = 'Unavailable',
                "PathOwnershipKey" = NULL,
                "ManagedRootFolderId" = NULL,
                "StateReason" = 'The persisted managed root no longer exists.',
                "DirectoryObjectIdentityUnavailableReason" =
                    coalesce(
                        "DirectoryObjectIdentityUnavailableReason",
                        'The persisted managed root no longer exists.')
            WHERE "State" <> 'Removed'
              AND "ManagedRootFolderId" IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1
                  FROM "RootFolders" AS "root"
                  WHERE "root"."Id" =
                      "LibraryDirectoryOwnerships"."ManagedRootFolderId");
            """);

        transaction.Commit();
        return removedRows + unavailableRows;
    }
}
