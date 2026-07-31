using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPhysicalFileIdentityAndMoveCleanupProtection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CleanupProtectionVersion",
                table: "MoveJobEntries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PhysicalIdentityObservedAtUtc",
                table: "AudiobookFiles",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PhysicalIdentityVersion",
                table: "AudiobookFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "PhysicalObjectIdentity",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CleanupProtectionVersion",
                table: "MoveJobEntries");

            migrationBuilder.DropColumn(
                name: "PhysicalIdentityObservedAtUtc",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PhysicalIdentityVersion",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(
                name: "PhysicalObjectIdentity",
                table: "AudiobookFiles");
        }
    }
}
