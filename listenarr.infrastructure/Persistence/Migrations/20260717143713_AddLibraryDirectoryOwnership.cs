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
        }
    }
}
