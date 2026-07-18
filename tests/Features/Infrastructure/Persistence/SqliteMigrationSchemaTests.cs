/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Listenarr.Tests.Features.Infrastructure.Persistence
{
    /// <summary>
    /// Migrations must be scaffolded with dotnet ef, and many tests still run
    /// on the EF InMemory provider — which never executes them. Missing migration
    /// metadata once let a migration ship without its [Migration] attribute + Designer
    /// (20251124102000_AddMoveJobSourcePath): EF discovery never saw it, so every
    /// SQLite install was missing MoveJobs.SourcePath while the model mapped it,
    /// and the first full-entity query failed at runtime ("no such column").
    /// These tests migrate a REAL SQLite database and verify the outcome so that
    /// class of drift fails CI instead of production.
    /// </summary>
    [Trait("Area", "Persistence")]
    [Trait("Name", "SqliteMigrationSchemaTests")]
    public class SqliteMigrationSchemaTests
    {
        public static TheoryData<string> ChangedMigrationIds => new()
        {
            "20251124102000_AddMoveJobSourcePath",
            "20260702200000_AddProcessExecutionLogs",
            "20260703024452_AddMoveJobDeleteEmptySource",
            "20260708223635_AddDurableFilesystemMoves",
            "20260708224312_AddMoveJobRelocationForeignKey",
            "20260708224430_ReconcileDurableMoveJobs",
            "20260708224705_AddMoveJobLeaseGeneration",
            "20260708224900_AddRootFolderRelocationSkippedItems",
            "20260708225028_MakeRootFolderRelocationRootNullable",
            "20260708225144_SetRootFolderRelocationRootDeleteBehavior",
            "20260710172532_AddMoveJobSourceCleanupBoundary",
            "20260713181804_HardenMoveExecutionAndScanHandoffs",
            "20260715115000_AddAudiobookFileOwnershipIdentity",
            "20260717143713_AddLibraryDirectoryOwnership"
        };

        private static (SqliteConnection Connection, ListenArrDbContext Context) CreateMigratedSqliteContext()
        {
            // Shared in-memory database lives as long as the connection is open.
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;

            var context = new ListenArrDbContext(options);
            context.Database.Migrate();
            return (connection, context);
        }

        [Fact]
        [Trait("Scenario", "EveryModelColumnExistsAfterMigrate")]
        public void EveryMappedColumn_ExistsInMigratedSqliteSchema()
        {
            var (connection, context) = CreateMigratedSqliteContext();
            using var _conn = connection;
            using var _ctx = context;

            var failures = new List<string>();

            foreach (var entityType in context.Model.GetEntityTypes())
            {
                var tableName = entityType.GetTableName();
                if (string.IsNullOrEmpty(tableName))
                {
                    continue; // not mapped to a table (owned/view/keyless)
                }

                var storeObject = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(tableName, entityType.GetSchema());
                var columns = entityType.GetProperties()
                    .Select(p => p.GetColumnName(storeObject))
                    .Where(c => !string.IsNullOrEmpty(c))
                    .Distinct()
                    .ToList();
                if (columns.Count == 0)
                {
                    continue;
                }

                // SELECT every mapped column with LIMIT 0: succeeds only when the
                // migrated schema actually contains each one.
                var columnList = string.Join(", ", columns.Select(c => $"\"{c}\""));
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT {columnList} FROM \"{tableName}\" LIMIT 0";
                try
                {
                    using var reader = command.ExecuteReader();
                }
                catch (SqliteException ex)
                {
                    failures.Add($"{tableName}: {ex.Message}");
                }
            }

            Assert.True(failures.Count == 0,
                "Model maps columns the migrated SQLite schema does not have — a migration is missing, "
                + "not discovered (missing [Migration] attribute / Designer), or out of sync:\n"
                + string.Join("\n", failures));
        }

        [Fact]
        [Trait("Scenario", "MigrationHistoryMatchesModel")]
        public async Task MigrationHistory_HasNoPendingModelChanges()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;

            await using var context = new ListenArrDbContext(options);
            await context.Database.MigrateAsync();

            Assert.False(
                context.Database.HasPendingModelChanges(),
                "The configured EF model differs from the accumulated migration snapshots. "
                + "Regenerate migrations with dotnet ef migrations add instead of hand-authoring them.");
        }

        [Fact]
        [Trait("Scenario", "AudiobookFileOwnershipIdentityDefaultsAndIndexes")]
        public async Task AudiobookFileOwnershipMigration_PreservesRowsAndCreatesOwnershipIndexes()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;

            await using var context = new ListenArrDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260713181804_HardenMoveExecutionAndScanHandoffs");
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "Audiobooks" ("Id", "Explicit", "Abridged", "Monitored")
                VALUES (1, 0, 0, 1)
                """);
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "AudiobookFiles" ("AudiobookId", "Path", "CreatedAt")
                VALUES (1, '/library/book-one.m4b', CURRENT_TIMESTAMP),
                       (1, '/library/book-two.m4b', CURRENT_TIMESTAMP)
                """);

            await migrator.MigrateAsync("20260715115000_AddAudiobookFileOwnershipIdentity");

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT "PathCaseSensitivity", "PathCaseSensitivityMode",
                           "PathIdentityVersion", "PathIdentityState"
                    FROM "AudiobookFiles"
                    ORDER BY "Id"
                    LIMIT 1
                    """;
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal("Unknown", reader.GetString(0));
                Assert.Equal("Auto", reader.GetString(1));
                Assert.Equal(1, reader.GetInt32(2));
                Assert.Equal("Unavailable", reader.GetString(3));
            }

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT COUNT(*)
                    FROM pragma_index_list('AudiobookFiles')
                    WHERE name IN (
                        'IX_AudiobookFiles_PathIdentityLookupKey',
                        'IX_AudiobookFiles_PathOwnershipKey')
                    """;
                Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
            }

            await context.Database.ExecuteSqlRawAsync(
                "UPDATE \"AudiobookFiles\" SET \"PathOwnershipKey\" = 'owned:path' WHERE \"Id\" = 1");
            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlRawAsync(
                    "UPDATE \"AudiobookFiles\" SET \"PathOwnershipKey\" = 'owned:path' WHERE \"Id\" = 2"));
        }

        [Fact]
        [Trait("Scenario", "LibraryDirectoryOwnershipIndexes")]
        public async Task LibraryDirectoryOwnershipMigration_CreatesDurableOwnershipIndexes()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;

            await using var context = new ListenArrDbContext(options);
            await context.Database.MigrateAsync();

            await using (var command = connection.CreateCommand())
            {
                command.CommandText =
                    """
                    SELECT COUNT(*)
                    FROM pragma_index_list('LibraryDirectoryOwnerships')
                    WHERE name IN (
                        'IX_LibraryDirectoryOwnerships_CreationOperationId_State',
                        'IX_LibraryDirectoryOwnerships_OwnershipToken',
                        'IX_LibraryDirectoryOwnerships_PathIdentityLookupKey',
                        'IX_LibraryDirectoryOwnerships_PathOwnershipKey')
                    """;
                Assert.Equal(4L, (long)(await command.ExecuteScalarAsync())!);
            }

            const string insertSql =
                """
                INSERT INTO "LibraryDirectoryOwnerships" (
                    "Path", "CanonicalPath", "PathSyntax", "PathCaseSensitivity",
                    "PathCaseSensitivityMode", "PathIdentityBoundary",
                    "PathIdentityLookupKey", "PathOwnershipKey", "OwnershipToken",
                    "State", "CreationWorkflow", "CreatedAt", "UpdatedAt")
                VALUES ({0}, {0}, 'Unix', 'Sensitive', 'Sensitive', '/library',
                    {1}, {2}, {3}, 'Owned', 'migration-test', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP)
                """;
            await context.Database.ExecuteSqlRawAsync(
                insertSql,
                "/library/author-one",
                "lookup:one",
                "ownership:one",
                "11111111111111111111111111111111");
            await context.Database.ExecuteSqlRawAsync(
                insertSql,
                "/library/author-two",
                "lookup:two",
                null,
                "22222222222222222222222222222222");

            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlRawAsync(
                    "UPDATE \"LibraryDirectoryOwnerships\" SET \"PathOwnershipKey\" = 'ownership:one' WHERE \"OwnershipToken\" = '22222222222222222222222222222222'"));
            await Assert.ThrowsAsync<SqliteException>(() =>
                context.Database.ExecuteSqlRawAsync(
                    insertSql,
                    "/library/author-three",
                    "lookup:three",
                    null,
                    "11111111111111111111111111111111"));
        }

        [Theory]
        [MemberData(nameof(ChangedMigrationIds))]
        [Trait("Scenario", "ChangedMigrationsDowngradeAndReapply")]
        public async Task ChangedMigration_CanDowngradeOneStepAndReapply(string migrationId)
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;

            await using var context = new ListenArrDbContext(options);
            var migrations = context.Database.GetMigrations().ToList();
            var migrationIndex = migrations.IndexOf(migrationId);
            Assert.True(
                migrationIndex > 0,
                $"Migration '{migrationId}' was not discovered or has no predecessor.");

            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrationId);
            Assert.Contains(migrationId, await context.Database.GetAppliedMigrationsAsync());

            await migrator.MigrateAsync(migrations[migrationIndex - 1]);
            Assert.DoesNotContain(migrationId, await context.Database.GetAppliedMigrationsAsync());

            await migrator.MigrateAsync(migrationId);
            Assert.Contains(migrationId, await context.Database.GetAppliedMigrationsAsync());
        }

        [Fact]
        [Trait("Scenario", "ExistingRowsReceiveValidEnumDefaults")]
        public async Task ExistingRows_MaterializeAfterDurableMoveMigrationAddsEnumColumns()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;
            var moveJobId = Guid.NewGuid();
            var enqueuedAt = DateTime.UtcNow;

            await using (var seedingContext = new ListenArrDbContext(options))
            {
                var migrator = seedingContext.GetService<IMigrator>();
                await migrator.MigrateAsync("20260703024452_AddMoveJobDeleteEmptySource");
                await seedingContext.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "RootFolders" ("Name", "Path", "IsDefault")
                    VALUES ({0}, {1}, {2})
                    """,
                    "Library",
                    "/library",
                    true);
                await seedingContext.Database.ExecuteSqlRawAsync(
                    """
                    INSERT INTO "MoveJobs" (
                        "Id", "AudiobookId", "RequestedPath", "EnqueuedAt", "Status",
                        "AttemptCount", "DeleteEmptySource", "SourcePath")
                    VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7})
                    """,
                    moveJobId,
                    42,
                    "/library/New Title",
                    enqueuedAt,
                    nameof(MoveJobStatus.Queued),
                    0,
                    true,
                    "/library/Old Title");

                await migrator.MigrateAsync();
            }

            await using var verification = new ListenArrDbContext(options);
            var root = await verification.RootFolders.SingleAsync();
            var moveJob = await verification.MoveJobs.SingleAsync();

            Assert.Equal(FileSystemCaseSensitivityMode.Auto, root.CaseSensitivityMode);
            Assert.Equal(PathIdentityState.Unavailable, root.PathIdentityState);
            Assert.Equal(FileSystemCaseSensitivity.Unknown, root.ResolvedCaseSensitivity);
            Assert.Equal(MoveFailureKind.None, moveJob.FailureKind);
            Assert.Equal(MoveJobPhase.None, moveJob.Phase);
        }

        [Fact]
        [Trait("Scenario", "NullableRelocationRootDowngradeFailsClosed")]
        public async Task NullableRelocationRoot_DowngradeRejectsOrphanHistoryWithoutCorruption()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;
            var relocationId = Guid.NewGuid();

            await using var context = new ListenArrDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260708225144_SetRootFolderRelocationRootDeleteBehavior");

            var root = new RootFolder
            {
                Name = "Deleted Library",
                Path = "/library",
                IsDefault = true
            };
            context.RootFolders.Add(root);
            await context.SaveChangesAsync();
            context.RootFolderRelocations.Add(new RootFolderRelocation
            {
                Id = relocationId,
                RootFolderId = root.Id,
                SourcePath = "/library",
                TargetPath = "/new-library",
                Mode = RootFolderRelocationMode.MetadataOnly,
                Status = RootFolderRelocationStatus.Completed,
                DesiredName = "Deleted Library",
                DesiredIsDefault = true,
                CompletedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();

            context.RootFolders.Remove(root);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            Assert.Null((await context.RootFolderRelocations.SingleAsync()).RootFolderId);

            var exception = await Assert.ThrowsAsync<SqliteException>(() =>
                migrator.MigrateAsync("20260708224900_AddRootFolderRelocationSkippedItems"));
            Assert.Contains(
                "CK_RootRelocationDowngrade_NoOrphanHistory",
                exception.Message,
                StringComparison.Ordinal);

            await using (var rootIdCommand = connection.CreateCommand())
            {
                rootIdCommand.CommandText =
                    "SELECT \"RootFolderId\" FROM \"RootFolderRelocations\" LIMIT 1;";
                Assert.Equal(DBNull.Value, await rootIdCommand.ExecuteScalarAsync());
            }

            await using (var foreignKeyCheck = connection.CreateCommand())
            {
                foreignKeyCheck.CommandText = "PRAGMA foreign_key_check;";
                await using var reader = await foreignKeyCheck.ExecuteReaderAsync();
                Assert.False(await reader.ReadAsync());
            }

            await migrator.MigrateAsync();
            Assert.Contains(
                "20260708225144_SetRootFolderRelocationRootDeleteBehavior",
                await context.Database.GetAppliedMigrationsAsync());
        }

        [Fact]
        [Trait("Scenario", "NullableRelocationRootDowngradePreservesValidHistory")]
        public async Task NullableRelocationRoot_DowngradePreservesHistoryWithExistingRoot()
        {
            await using var connection = new SqliteConnection("DataSource=:memory:");
            await connection.OpenAsync();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;

            await using var context = new ListenArrDbContext(options);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync("20260708225144_SetRootFolderRelocationRootDeleteBehavior");

            var root = new RootFolder
            {
                Name = "Retained Library",
                Path = "/library",
                IsDefault = true
            };
            context.RootFolders.Add(root);
            await context.SaveChangesAsync();
            context.RootFolderRelocations.Add(new RootFolderRelocation
            {
                Id = Guid.NewGuid(),
                RootFolderId = root.Id,
                SourcePath = "/library",
                TargetPath = "/new-library",
                Mode = RootFolderRelocationMode.MetadataOnly,
                Status = RootFolderRelocationStatus.Completed,
                DesiredName = "Retained Library",
                DesiredIsDefault = true,
                CompletedAt = DateTime.UtcNow
            });
            await context.SaveChangesAsync();

            await migrator.MigrateAsync("20260708224900_AddRootFolderRelocationSkippedItems");

            await using (var rootIdCommand = connection.CreateCommand())
            {
                rootIdCommand.CommandText =
                    "SELECT \"RootFolderId\" FROM \"RootFolderRelocations\" LIMIT 1;";
                Assert.Equal((long)root.Id, await rootIdCommand.ExecuteScalarAsync());
            }

            await using (var foreignKeyCheck = connection.CreateCommand())
            {
                foreignKeyCheck.CommandText = "PRAGMA foreign_key_check;";
                await using var reader = await foreignKeyCheck.ExecuteReaderAsync();
                Assert.False(await reader.ReadAsync());
            }

            await migrator.MigrateAsync();
            Assert.Contains(
                "20260708225144_SetRootFolderRelocationRootDeleteBehavior",
                await context.Database.GetAppliedMigrationsAsync());
        }

        [Fact]
        [Trait("Scenario", "MoveJobsSourcePathRegression")]
        public void MoveJobs_SourcePathColumn_ExistsAfterMigrate()
        {
            var (connection, context) = CreateMigratedSqliteContext();
            using var _conn = connection;
            using var _ctx = context;

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM pragma_table_info('MoveJobs')";
            var columns = new List<string>();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    columns.Add(reader.GetString(0));
                }
            }

            Assert.Contains("SourcePath", columns);
        }
    }
}
