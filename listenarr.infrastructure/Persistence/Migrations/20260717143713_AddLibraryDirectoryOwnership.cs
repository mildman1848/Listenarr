using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryDirectoryOwnership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CanonicalPath",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PathCaseSensitivity",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PathCaseSensitivityMode",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PathIdentityBoundary",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PathIdentityLookupKey",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PathIdentityReason",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PathIdentityState",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "PathIdentityVersion",
                table: "AudiobookFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PathOwnershipKey",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PathSyntax",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "LibraryDirectoryOwnerships",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Path = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    CanonicalPath = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    PathSyntax = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PathCaseSensitivity = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PathCaseSensitivityMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    PathIdentityBoundary = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    PathIdentityLookupKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    PathOwnershipKey = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    OwnershipToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreationWorkflow = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreationOperationId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AudiobookId = table.Column<int>(type: "INTEGER", nullable: true),
                    StateReason = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryDirectoryOwnerships", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookFiles_PathIdentityLookupKey",
                table: "AudiobookFiles",
                column: "PathIdentityLookupKey");

            migrationBuilder.CreateIndex(
                name: "IX_AudiobookFiles_PathOwnershipKey",
                table: "AudiobookFiles",
                column: "PathOwnershipKey",
                unique: true,
                filter: "\"PathOwnershipKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnerships_CreationOperationId_State",
                table: "LibraryDirectoryOwnerships",
                columns: new[] { "CreationOperationId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnerships_OwnershipToken",
                table: "LibraryDirectoryOwnerships",
                column: "OwnershipToken",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnerships_PathIdentityLookupKey",
                table: "LibraryDirectoryOwnerships",
                column: "PathIdentityLookupKey");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnerships_PathOwnershipKey",
                table: "LibraryDirectoryOwnerships",
                column: "PathOwnershipKey",
                unique: true,
                filter: "\"PathOwnershipKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryDirectoryOwnerships");

            migrationBuilder.DropIndex(
                name: "IX_AudiobookFiles_PathIdentityLookupKey",
                table: "AudiobookFiles");

            migrationBuilder.DropIndex(
                name: "IX_AudiobookFiles_PathOwnershipKey",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "CanonicalPath",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathCaseSensitivity",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathCaseSensitivityMode",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathIdentityBoundary",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathIdentityLookupKey",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathIdentityReason",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathIdentityState",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathIdentityVersion",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathOwnershipKey",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PathSyntax",
                table: "AudiobookFiles");
        }
    }
}
