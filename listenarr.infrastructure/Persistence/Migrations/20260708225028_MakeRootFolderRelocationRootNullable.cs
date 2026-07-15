using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MakeRootFolderRelocationRootNullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "RootFolderId",
                table: "RootFolderRelocations",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "INTEGER");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The previous schema cannot represent relocation history after its root folder
            // has been deleted. Fail before EF's SQLite table rebuild can coerce NULL to 0 and
            // silently create a dangling foreign key.
            migrationBuilder.Sql(
                """
                CREATE TEMP TABLE IF NOT EXISTS "__ListenarrRootRelocationDowngradeGuard" (
                    "Value" INTEGER NOT NULL,
                    CONSTRAINT "CK_RootRelocationDowngrade_NoOrphanHistory" CHECK ("Value" = 0)
                );
                """);
            migrationBuilder.Sql(
                "DELETE FROM \"__ListenarrRootRelocationDowngradeGuard\";");
            migrationBuilder.Sql(
                """
                INSERT INTO "__ListenarrRootRelocationDowngradeGuard" ("Value")
                SELECT 1
                FROM "RootFolderRelocations"
                WHERE "RootFolderId" IS NULL
                LIMIT 1;
                """);
            migrationBuilder.Sql(
                "DROP TABLE \"__ListenarrRootRelocationDowngradeGuard\";");

            migrationBuilder.AlterColumn<int>(
                name: "RootFolderId",
                table: "RootFolderRelocations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "INTEGER",
                oldNullable: true);
        }
    }
}
