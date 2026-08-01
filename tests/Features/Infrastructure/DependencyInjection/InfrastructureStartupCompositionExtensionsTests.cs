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
using System.Data.Common;
using Listenarr.Infrastructure.DependencyInjection;
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Listenarr.Tests.Features.Infrastructure.DependencyInjection;

[Trait("Name", "InfrastructureStartupCompositionExtensionsTests")]
[Trait("Category", "Infrastructure")]
public sealed class InfrastructureStartupCompositionExtensionsTests : BaseTests
{
    [Fact]
    [Trait("Scenario", "LegacyOwnershipForeignKeyMigration")]
    public void ApplyListenarrDatabaseMigrations_RepairsLegacyOwnershipBeforeForeignKey()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var baselineOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
            .UseSqlite(connection, sqlite =>
                sqlite.MigrationsAssembly(
                    typeof(ListenArrDbContext).Assembly.GetName().Name))
            .Options;
        using (var baseline = new ListenArrDbContext(baselineOptions))
        {
            baseline.GetService<IMigrator>().Migrate(
                LibraryDirectoryOwnershipMigrationPreflight.PredecessorMigrationId);
            baseline.Database.ExecuteSqlRaw(
                """
                INSERT INTO "LibraryDirectoryOwnerships" (
                    "Id", "Path", "CanonicalPath", "PathSyntax",
                    "PathCaseSensitivity", "PathCaseSensitivityMode",
                    "PathIdentityBoundary", "PathIdentityLookupKey",
                    "PathOwnershipKey", "OwnershipToken", "State",
                    "CreationWorkflow", "CreatedAt", "UpdatedAt",
                    "ManagedRootFolderId")
                VALUES (
                    404, '/orphan', '/orphan', 'Unix', 'Sensitive',
                    'Sensitive', '/orphan', 'lookup-404', 'ownership-404',
                    '40440440440440440440440440440440', 'Owned', 'test',
                    '2026-08-01T00:00:00Z', '2026-08-01T00:00:00Z', 999);
                """);
        }

        var services = new ServiceCollection();
        services.AddDbContextFactory<ListenArrDbContext>(options =>
            options
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(
                        typeof(ListenArrDbContext).Assembly.GetName().Name))
                .ConfigureWarnings(warnings => warnings.Throw(
                    RelationalEventId.NonTransactionalMigrationOperationWarning)));
        using var provider = services.BuildServiceProvider();

        provider.ApplyListenarrDatabaseMigrations();

        var factory = provider.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
        using var verification = factory.CreateDbContext();
        var ownership = verification.LibraryDirectoryOwnerships.Single(
            candidate => candidate.Id == 404);
        Assert.Equal(LibraryDirectoryOwnershipState.Unavailable, ownership.State);
        Assert.Null(ownership.ManagedRootFolderId);
        Assert.Null(ownership.PathOwnershipKey);
        Assert.Contains(
            LibraryDirectoryOwnershipMigrationPreflight.ForeignKeyMigrationId,
            verification.Database.GetAppliedMigrations());
    }

    [Fact]
    [Trait("Scenario", "MigrationFailureFailsStartupClosed")]
    public void ApplyListenarrDatabaseMigrations_MigrationFailurePropagates()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        var services = new ServiceCollection();
        services.AddDbContextFactory<ListenArrDbContext>(options =>
            options
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(
                        typeof(ListenArrDbContext).Assembly.GetName().Name))
                .AddInterceptors(new ThrowingMigrationCommandInterceptor()));
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            provider.ApplyListenarrDatabaseMigrations());

        Assert.Equal("Injected migration failure.", exception.Message);
    }

    private sealed class ThrowingMigrationCommandInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result)
        {
            throw new InvalidOperationException("Injected migration failure.");
        }
    }
}
