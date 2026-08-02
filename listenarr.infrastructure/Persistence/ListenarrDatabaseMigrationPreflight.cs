using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence;

internal static class ListenarrDatabaseMigrationPreflight
{
    internal const string DurableMoveSchemaMigrationId =
        "20260708223635_AddDurableFilesystemMoves";
    internal const string RootFoldersMigrationId =
        "20260101172733_AddRootFolders";
    internal const string DirectoryObjectIdentityMigrationId =
        "20260726042801_AddDirectoryObjectIdentityAuthorization";
    internal const string AudiobookFileOwnershipMigrationId =
        "20260717143713_AddLibraryDirectoryOwnership";

    public static ListenarrDatabaseMigrationPreflightResult RepairLegacyData(
        ListenArrDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var applied = context.Database.GetAppliedMigrations()
            .ToHashSet(StringComparer.Ordinal);
        var normalizeDefaultRoots =
            applied.Contains(RootFoldersMigrationId)
            && !applied.Contains(DirectoryObjectIdentityMigrationId);
        if (!normalizeDefaultRoots)
        {
            return default;
        }

        using var transaction = context.Database.BeginTransaction();
        var defaultRootsNormalized = context.Database.ExecuteSqlRaw(
            """
            UPDATE "RootFolders"
            SET "IsDefault" = 0
            WHERE "IsDefault" = 1
              AND "Id" <> (
                  SELECT MIN("Id")
                  FROM "RootFolders"
                  WHERE "IsDefault" = 1
              );
            """);
        transaction.Commit();

        return new ListenarrDatabaseMigrationPreflightResult(defaultRootsNormalized);
    }

    public static ListenarrDatabasePostMigrationRepairResult RepairPostMigrationData(
        ListenArrDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var applied = context.Database.GetAppliedMigrations()
            .ToHashSet(StringComparer.Ordinal);
        var repairLegacyMoveJobs = applied.Contains(DurableMoveSchemaMigrationId);
        var repairAudiobookFileIdentityDefaults =
            applied.Contains(AudiobookFileOwnershipMigrationId);
        if (!repairLegacyMoveJobs && !repairAudiobookFileIdentityDefaults)
        {
            return default;
        }

        using var transaction = context.Database.BeginTransaction();
        // AddDurableFilesystemMoves gives pre-existing rows IdentityKeyVersion = 0.
        // Treat that generated default as the durable one-time repair marker so normal
        // startup retries cannot overwrite identity keys created by current code.
        var moveJobsRepaired = repairLegacyMoveJobs
            ? context.Database.ExecuteSqlRaw(
                """
                UPDATE "MoveJobs"
                SET
                    "Status" = CASE
                        WHEN "Status" = 'Processing' THEN 'Running'
                        ELSE "Status"
                    END,
                    "IdentityKeyVersion" = 1,
                    "ActiveDeduplicationKey" = 'legacy:' || "Id"
                WHERE "IdentityKeyVersion" = 0
                  AND "Status" IN ('Queued', 'Processing', 'Running', 'RetryScheduled');
                """)
            : 0;
        var audiobookFilesRepaired = repairAudiobookFileIdentityDefaults
            ? context.Database.ExecuteSqlRaw(
                """
                UPDATE "AudiobookFiles"
                SET
                    "PathCaseSensitivity" = CASE
                        WHEN "PathCaseSensitivity" = '' THEN 'Unknown'
                        ELSE "PathCaseSensitivity"
                    END,
                    "PathCaseSensitivityMode" = CASE
                        WHEN "PathCaseSensitivityMode" = '' THEN 'Auto'
                        ELSE "PathCaseSensitivityMode"
                    END,
                    "PathIdentityVersion" = CASE
                        WHEN "PathIdentityVersion" = 0 THEN 1
                        ELSE "PathIdentityVersion"
                    END,
                    "PathIdentityState" = CASE
                        WHEN "PathIdentityState" = '' THEN 'Unavailable'
                        ELSE "PathIdentityState"
                    END
                WHERE "PathCaseSensitivity" = ''
                   OR "PathCaseSensitivityMode" = ''
                   OR "PathIdentityVersion" = 0
                   OR "PathIdentityState" = '';
                """)
            : 0;
        transaction.Commit();
        return new ListenarrDatabasePostMigrationRepairResult(
            moveJobsRepaired,
            audiobookFilesRepaired);
    }
}

internal readonly record struct ListenarrDatabaseMigrationPreflightResult(
    int DefaultRootsNormalized);

internal readonly record struct ListenarrDatabasePostMigrationRepairResult(
    int MoveJobsRepaired,
    int AudiobookFilesRepaired);
