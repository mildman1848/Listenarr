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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
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
