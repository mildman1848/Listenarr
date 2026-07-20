using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSingleDefaultRootFolder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
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

            migrationBuilder.CreateIndex(
                name: "IX_RootFolders_SingleDefault",
                table: "RootFolders",
                column: "IsDefault",
                unique: true,
                filter: "\"IsDefault\" = 1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RootFolders_SingleDefault",
                table: "RootFolders");
        }
    }
}
