using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddOwnershipRecoveryProtocols : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                AddOwnershipForeignKeySql(),
                suppressTransaction: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetIdentityEnrollmentState",
                table: "RootFolderRelocations",
                type: "TEXT",
                maxLength: 24,
                nullable: false,
                defaultValue: "Authorized");

            migrationBuilder.CreateTable(
                name: "LibraryDirectoryOwnershipPathMigrations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnershipId = table.Column<long>(type: "INTEGER", nullable: false),
                    RelocationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceCanonicalPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SourcePathSyntax = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SourceCaseSensitivity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SourceCaseSensitivityMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    SourceIdentityBoundary = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    SourceIdentityLookupKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    SourceOwnershipKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    TargetCanonicalPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    TargetPathSyntax = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetCaseSensitivity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetCaseSensitivityMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    TargetIdentityBoundary = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    TargetIdentityLookupKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    TargetOwnershipKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryDirectoryOwnershipPathMigrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LibraryDirectoryOwnershipPathMigrations_LibraryDirectoryOwnerships_OwnershipId",
                        column: x => x.OwnershipId,
                        principalTable: "LibraryDirectoryOwnerships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_LibraryDirectoryOwnershipPathMigrations_RootFolderRelocations_RelocationId",
                        column: x => x.RelocationId,
                        principalTable: "RootFolderRelocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LibraryDirectoryOwnershipRetiredMarkers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OwnershipId = table.Column<long>(type: "INTEGER", nullable: false),
                    OwnershipToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CanonicalMarkerPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: true),
                    CanonicalOwnershipPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    PathSyntax = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PathCaseSensitivity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PathCaseSensitivityMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PathIdentityBoundary = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    CanonicalPayload = table.Column<string>(type: "TEXT", maxLength: 16384, nullable: true),
                    PayloadSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PayloadVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    OriginalManagedRootFolderId = table.Column<int>(type: "INTEGER", nullable: true),
                    DirectoryObjectIdentityVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    DirectoryObjectIdentity = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryDirectoryOwnershipRetiredMarkers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LibraryDirectoryOwnershipRetiredMarkers_LibraryDirectoryOwnerships_OwnershipId",
                        column: x => x.OwnershipId,
                        principalTable: "LibraryDirectoryOwnerships",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RootFolderRelocationCreatedDirectories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RelocationId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CanonicalPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    OwnershipToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DirectoryObjectIdentityVersion = table.Column<int>(type: "INTEGER", nullable: true),
                    DirectoryObjectIdentity = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RootFolderRelocationCreatedDirectories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RootFolderRelocationCreatedDirectories_RootFolderRelocations_RelocationId",
                        column: x => x.RelocationId,
                        principalTable: "RootFolderRelocations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                """
                UPDATE "RootFolderRelocations"
                SET "TargetIdentityEnrollmentState" =
                    CASE
                        WHEN "Status" IN ('Completed', 'Failed')
                            THEN 'NotRequired'
                        WHEN "TargetDirectoryObjectIdentityVersion" IS NOT NULL
                            AND trim(coalesce("TargetDirectoryObjectIdentity", '')) <> ''
                            AND trim(coalesce("TargetDirectoryObjectIdentityUnavailableReason", '')) = ''
                            THEN 'Authorized'
                        WHEN "Status" IN ('Pending', 'Running', 'NeedsAttention')
                            AND "TargetDirectoryObjectIdentityVersion" IS NULL
                            AND trim(coalesce("TargetDirectoryObjectIdentity", '')) = ''
                            AND trim(coalesce("TargetDirectoryObjectIdentityUnavailableReason", '')) = ''
                            THEN 'LegacyUnenrolled'
                        ELSE 'Unavailable'
                    END;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO "LibraryDirectoryOwnershipRetiredMarkers" (
                    "OwnershipId",
                    "OwnershipToken",
                    "CanonicalMarkerPath",
                    "CanonicalOwnershipPath",
                    "PathSyntax",
                    "PathCaseSensitivity",
                    "PathCaseSensitivityMode",
                    "PathIdentityBoundary",
                    "CanonicalPayload",
                    "PayloadSha256",
                    "PayloadVersion",
                    "OriginalManagedRootFolderId",
                    "DirectoryObjectIdentityVersion",
                    "DirectoryObjectIdentity",
                    "State",
                    "CreatedAt",
                    "UpdatedAt")
                SELECT
                    "Id",
                    "OwnershipToken",
                    NULL,
                    "CanonicalPath",
                    "PathSyntax",
                    "PathCaseSensitivity",
                    "PathCaseSensitivityMode",
                    "PathIdentityBoundary",
                    NULL,
                    NULL,
                    CASE
                        WHEN "ManagedRootFolderId" IS NOT NULL
                            AND "DirectoryObjectIdentityVersion" IS NOT NULL
                            AND trim(coalesce("DirectoryObjectIdentity", '')) <> ''
                            THEN 2
                        ELSE 1
                    END,
                    "ManagedRootFolderId",
                    "DirectoryObjectIdentityVersion",
                    "DirectoryObjectIdentity",
                    'Pending',
                    "CreatedAt",
                    "UpdatedAt"
                FROM "LibraryDirectoryOwnerships"
                WHERE "State" = 'Removed';

                UPDATE "LibraryDirectoryOwnerships"
                SET "ManagedRootFolderId" = NULL
                WHERE "State" = 'Removed';

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

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnershipPathMigrations_OwnershipId_RelocationId",
                table: "LibraryDirectoryOwnershipPathMigrations",
                columns: new[] { "OwnershipId", "RelocationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnershipPathMigrations_RelocationId",
                table: "LibraryDirectoryOwnershipPathMigrations",
                column: "RelocationId");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnershipPathMigrations_TargetOwnershipKey",
                table: "LibraryDirectoryOwnershipPathMigrations",
                column: "TargetOwnershipKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnershipRetiredMarkers_CanonicalMarkerPath",
                table: "LibraryDirectoryOwnershipRetiredMarkers",
                column: "CanonicalMarkerPath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnershipRetiredMarkers_OwnershipId",
                table: "LibraryDirectoryOwnershipRetiredMarkers",
                column: "OwnershipId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RootFolderRelocationCreatedDirectories_OwnershipToken",
                table: "RootFolderRelocationCreatedDirectories",
                column: "OwnershipToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RootFolderRelocationCreatedDirectories_RelocationId_CanonicalPath",
                table: "RootFolderRelocationCreatedDirectories",
                columns: new[] { "RelocationId", "CanonicalPath" },
                unique: true);

            static string AddOwnershipForeignKeySql() =>
                """
                PRAGMA foreign_keys = 0;
                BEGIN IMMEDIATE;

                CREATE TABLE "ef_temp_LibraryDirectoryOwnerships" (
                    "Id" INTEGER NOT NULL
                        CONSTRAINT "PK_LibraryDirectoryOwnerships"
                        PRIMARY KEY AUTOINCREMENT,
                    "AudiobookId" INTEGER NULL,
                    "CanonicalPath" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "CreationOperationId" TEXT NULL,
                    "CreationWorkflow" TEXT NOT NULL,
                    "DirectoryObjectIdentity" TEXT NULL,
                    "DirectoryObjectIdentityUnavailableReason" TEXT NULL,
                    "DirectoryObjectIdentityVersion" INTEGER NULL,
                    "ManagedRootFolderId" INTEGER NULL,
                    "OwnershipToken" TEXT NOT NULL,
                    "Path" TEXT NOT NULL,
                    "PathCaseSensitivity" TEXT NOT NULL,
                    "PathCaseSensitivityMode" TEXT NOT NULL,
                    "PathIdentityBoundary" TEXT NOT NULL,
                    "PathIdentityLookupKey" TEXT NOT NULL,
                    "PathOwnershipKey" TEXT NULL,
                    "PathSyntax" TEXT NOT NULL,
                    "State" TEXT NOT NULL,
                    "StateReason" TEXT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    CONSTRAINT
                        "FK_LibraryDirectoryOwnerships_RootFolders_ManagedRootFolderId"
                        FOREIGN KEY ("ManagedRootFolderId")
                        REFERENCES "RootFolders" ("Id")
                        ON DELETE SET NULL);

                INSERT INTO "ef_temp_LibraryDirectoryOwnerships" (
                    "Id", "AudiobookId", "CanonicalPath", "CreatedAt",
                    "CreationOperationId", "CreationWorkflow",
                    "DirectoryObjectIdentity",
                    "DirectoryObjectIdentityUnavailableReason",
                    "DirectoryObjectIdentityVersion", "ManagedRootFolderId",
                    "OwnershipToken", "Path", "PathCaseSensitivity",
                    "PathCaseSensitivityMode", "PathIdentityBoundary",
                    "PathIdentityLookupKey", "PathOwnershipKey", "PathSyntax",
                    "State", "StateReason", "UpdatedAt")
                SELECT
                    "Id", "AudiobookId", "CanonicalPath", "CreatedAt",
                    "CreationOperationId", "CreationWorkflow",
                    "DirectoryObjectIdentity",
                    "DirectoryObjectIdentityUnavailableReason",
                    "DirectoryObjectIdentityVersion", "ManagedRootFolderId",
                    "OwnershipToken", "Path", "PathCaseSensitivity",
                    "PathCaseSensitivityMode", "PathIdentityBoundary",
                    "PathIdentityLookupKey", "PathOwnershipKey", "PathSyntax",
                    "State", "StateReason", "UpdatedAt"
                FROM "LibraryDirectoryOwnerships";
                DROP TABLE "LibraryDirectoryOwnerships";
                ALTER TABLE "ef_temp_LibraryDirectoryOwnerships"
                    RENAME TO "LibraryDirectoryOwnerships";
                CREATE INDEX
                    "IX_LibraryDirectoryOwnerships_CreationOperationId_State"
                    ON "LibraryDirectoryOwnerships"
                    ("CreationOperationId", "State");
                CREATE INDEX
                    "IX_LibraryDirectoryOwnerships_ManagedRootFolderId"
                    ON "LibraryDirectoryOwnerships" ("ManagedRootFolderId");
                CREATE UNIQUE INDEX
                    "IX_LibraryDirectoryOwnerships_OwnershipToken"
                    ON "LibraryDirectoryOwnerships" ("OwnershipToken");
                CREATE INDEX
                    "IX_LibraryDirectoryOwnerships_PathIdentityLookupKey"
                    ON "LibraryDirectoryOwnerships"
                    ("PathIdentityLookupKey");
                CREATE UNIQUE INDEX
                    "IX_LibraryDirectoryOwnerships_PathOwnershipKey"
                    ON "LibraryDirectoryOwnerships" ("PathOwnershipKey")
                    WHERE "PathOwnershipKey" IS NOT NULL;

                COMMIT;
                PRAGMA foreign_keys = 1;
                """;
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TEMP TABLE "__OwnershipRecoveryDownGuard" (
                    "Value" INTEGER NOT NULL CHECK ("Value" = 0));

                INSERT INTO "__OwnershipRecoveryDownGuard" ("Value")
                SELECT CASE WHEN
                    EXISTS (
                        SELECT 1
                        FROM "RootFolderRelocations"
                        WHERE "Status" IN ('Pending', 'Running', 'NeedsAttention'))
                    OR EXISTS (
                        SELECT 1
                        FROM "RootFolderRelocations"
                        WHERE "TargetIdentityEnrollmentState"
                            IN ('LegacyUnenrolled', 'Unavailable'))
                    OR EXISTS (
                        SELECT 1
                        FROM "LibraryDirectoryOwnershipPathMigrations")
                    OR EXISTS (
                        SELECT 1
                        FROM "RootFolderRelocationCreatedDirectories"
                        WHERE "State" IN ('Planned', 'Created'))
                    OR EXISTS (
                        SELECT 1
                        FROM "LibraryDirectoryOwnershipRetiredMarkers"
                        WHERE "State" = 'Pending')
                    OR EXISTS (
                        SELECT 1
                        FROM "LibraryDirectoryOwnershipRetiredMarkers" AS "retired"
                        WHERE "retired"."OriginalManagedRootFolderId" IS NOT NULL
                            AND NOT EXISTS (
                                SELECT 1
                                FROM "RootFolders" AS "root"
                                WHERE "root"."Id" =
                                    "retired"."OriginalManagedRootFolderId"))
                    THEN 1 ELSE 0 END;

                DROP TABLE "__OwnershipRecoveryDownGuard";
                """);

            migrationBuilder.Sql(
                """
                PRAGMA foreign_keys = 0;
                BEGIN IMMEDIATE;

                UPDATE "LibraryDirectoryOwnerships"
                SET "ManagedRootFolderId" = (
                    SELECT "retired"."OriginalManagedRootFolderId"
                    FROM "LibraryDirectoryOwnershipRetiredMarkers" AS "retired"
                    WHERE "retired"."OwnershipId" =
                        "LibraryDirectoryOwnerships"."Id")
                WHERE "State" = 'Removed'
                    AND EXISTS (
                        SELECT 1
                        FROM "LibraryDirectoryOwnershipRetiredMarkers" AS "retired"
                        WHERE "retired"."OwnershipId" =
                            "LibraryDirectoryOwnerships"."Id"
                            AND "retired"."OriginalManagedRootFolderId"
                                IS NOT NULL);

                CREATE TABLE "ef_temp_LibraryDirectoryOwnerships" (
                    "Id" INTEGER NOT NULL
                        CONSTRAINT "PK_LibraryDirectoryOwnerships"
                        PRIMARY KEY AUTOINCREMENT,
                    "AudiobookId" INTEGER NULL,
                    "CanonicalPath" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "CreationOperationId" TEXT NULL,
                    "CreationWorkflow" TEXT NOT NULL,
                    "DirectoryObjectIdentity" TEXT NULL,
                    "DirectoryObjectIdentityUnavailableReason" TEXT NULL,
                    "DirectoryObjectIdentityVersion" INTEGER NULL,
                    "ManagedRootFolderId" INTEGER NULL,
                    "OwnershipToken" TEXT NOT NULL,
                    "Path" TEXT NOT NULL,
                    "PathCaseSensitivity" TEXT NOT NULL,
                    "PathCaseSensitivityMode" TEXT NOT NULL,
                    "PathIdentityBoundary" TEXT NOT NULL,
                    "PathIdentityLookupKey" TEXT NOT NULL,
                    "PathOwnershipKey" TEXT NULL,
                    "PathSyntax" TEXT NOT NULL,
                    "State" TEXT NOT NULL,
                    "StateReason" TEXT NULL,
                    "UpdatedAt" TEXT NOT NULL);

                INSERT INTO "ef_temp_LibraryDirectoryOwnerships" (
                    "Id", "AudiobookId", "CanonicalPath", "CreatedAt",
                    "CreationOperationId", "CreationWorkflow",
                    "DirectoryObjectIdentity",
                    "DirectoryObjectIdentityUnavailableReason",
                    "DirectoryObjectIdentityVersion", "ManagedRootFolderId",
                    "OwnershipToken", "Path", "PathCaseSensitivity",
                    "PathCaseSensitivityMode", "PathIdentityBoundary",
                    "PathIdentityLookupKey", "PathOwnershipKey", "PathSyntax",
                    "State", "StateReason", "UpdatedAt")
                SELECT
                    "Id", "AudiobookId", "CanonicalPath", "CreatedAt",
                    "CreationOperationId", "CreationWorkflow",
                    "DirectoryObjectIdentity",
                    "DirectoryObjectIdentityUnavailableReason",
                    "DirectoryObjectIdentityVersion", "ManagedRootFolderId",
                    "OwnershipToken", "Path", "PathCaseSensitivity",
                    "PathCaseSensitivityMode", "PathIdentityBoundary",
                    "PathIdentityLookupKey", "PathOwnershipKey", "PathSyntax",
                    "State", "StateReason", "UpdatedAt"
                FROM "LibraryDirectoryOwnerships";
                DROP TABLE "LibraryDirectoryOwnerships";
                ALTER TABLE "ef_temp_LibraryDirectoryOwnerships"
                    RENAME TO "LibraryDirectoryOwnerships";
                CREATE INDEX
                    "IX_LibraryDirectoryOwnerships_CreationOperationId_State"
                    ON "LibraryDirectoryOwnerships"
                    ("CreationOperationId", "State");
                CREATE INDEX
                    "IX_LibraryDirectoryOwnerships_ManagedRootFolderId"
                    ON "LibraryDirectoryOwnerships" ("ManagedRootFolderId");
                CREATE UNIQUE INDEX
                    "IX_LibraryDirectoryOwnerships_OwnershipToken"
                    ON "LibraryDirectoryOwnerships" ("OwnershipToken");
                CREATE INDEX
                    "IX_LibraryDirectoryOwnerships_PathIdentityLookupKey"
                    ON "LibraryDirectoryOwnerships"
                    ("PathIdentityLookupKey");
                CREATE UNIQUE INDEX
                    "IX_LibraryDirectoryOwnerships_PathOwnershipKey"
                    ON "LibraryDirectoryOwnerships" ("PathOwnershipKey")
                    WHERE "PathOwnershipKey" IS NOT NULL;

                COMMIT;
                PRAGMA foreign_keys = 1;
                """,
                suppressTransaction: true);

            migrationBuilder.DropTable(
                name: "LibraryDirectoryOwnershipPathMigrations");

            migrationBuilder.DropTable(
                name: "LibraryDirectoryOwnershipRetiredMarkers");

            migrationBuilder.DropTable(
                name: "RootFolderRelocationCreatedDirectories");

            migrationBuilder.DropColumn(
                name: "TargetIdentityEnrollmentState",
                table: "RootFolderRelocations");
        }
    }
}
