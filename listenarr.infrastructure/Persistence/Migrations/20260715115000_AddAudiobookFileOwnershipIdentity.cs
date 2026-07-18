using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    public partial class AddAudiobookFileOwnershipIdentity : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CanonicalPath",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 4096,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PathSyntax",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PathCaseSensitivity",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unknown");

            migrationBuilder.AddColumn<string>(
                name: "PathCaseSensitivityMode",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Auto");

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
                name: "PathOwnershipKey",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PathIdentityVersion",
                table: "AudiobookFiles",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "PathIdentityState",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "Unavailable");

            migrationBuilder.AddColumn<string>(
                name: "PathIdentityReason",
                table: "AudiobookFiles",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

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
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AudiobookFiles_PathIdentityLookupKey",
                table: "AudiobookFiles");

            migrationBuilder.DropIndex(
                name: "IX_AudiobookFiles_PathOwnershipKey",
                table: "AudiobookFiles");

            migrationBuilder.DropColumn(name: "CanonicalPath", table: "AudiobookFiles");
            migrationBuilder.DropColumn(name: "PathSyntax", table: "AudiobookFiles");
            migrationBuilder.DropColumn(name: "PathCaseSensitivity", table: "AudiobookFiles");
            migrationBuilder.DropColumn(name: "PathCaseSensitivityMode", table: "AudiobookFiles");
            migrationBuilder.DropColumn(name: "PathIdentityBoundary", table: "AudiobookFiles");
            migrationBuilder.DropColumn(name: "PathIdentityLookupKey", table: "AudiobookFiles");
            migrationBuilder.DropColumn(name: "PathOwnershipKey", table: "AudiobookFiles");
            migrationBuilder.DropColumn(name: "PathIdentityVersion", table: "AudiobookFiles");
            migrationBuilder.DropColumn(name: "PathIdentityState", table: "AudiobookFiles");
            migrationBuilder.DropColumn(name: "PathIdentityReason", table: "AudiobookFiles");
        }
    }
}
