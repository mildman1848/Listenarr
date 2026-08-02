using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddDirectoryObjectIdentityAuthorization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DirectoryObjectIdentity",
                table: "RootFolders",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DirectoryObjectIdentityUnavailableReason",
                table: "RootFolders",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DirectoryObjectIdentityVersion",
                table: "RootFolders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetDirectoryObjectIdentity",
                table: "RootFolderRelocations",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetDirectoryObjectIdentityUnavailableReason",
                table: "RootFolderRelocations",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TargetDirectoryObjectIdentityVersion",
                table: "RootFolderRelocations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DirectoryObjectIdentity",
                table: "LibraryDirectoryOwnerships",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DirectoryObjectIdentityUnavailableReason",
                table: "LibraryDirectoryOwnerships",
                type: "TEXT",
                maxLength: 1024,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DirectoryObjectIdentityVersion",
                table: "LibraryDirectoryOwnerships",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ManagedRootFolderId",
                table: "LibraryDirectoryOwnerships",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_RootFolders_SingleDefault",
                table: "RootFolders",
                column: "IsDefault",
                unique: true,
                filter: "\"IsDefault\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryDirectoryOwnerships_ManagedRootFolderId",
                table: "LibraryDirectoryOwnerships",
                column: "ManagedRootFolderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_RootFolders_SingleDefault",
                table: "RootFolders");

            migrationBuilder.DropIndex(
                name: "IX_LibraryDirectoryOwnerships_ManagedRootFolderId",
                table: "LibraryDirectoryOwnerships");

            migrationBuilder.DropColumn(
                name: "DirectoryObjectIdentity",
                table: "RootFolders");

            migrationBuilder.DropColumn(
                name: "DirectoryObjectIdentityUnavailableReason",
                table: "RootFolders");

            migrationBuilder.DropColumn(
                name: "DirectoryObjectIdentityVersion",
                table: "RootFolders");

            migrationBuilder.DropColumn(
                name: "TargetDirectoryObjectIdentity",
                table: "RootFolderRelocations");

            migrationBuilder.DropColumn(
                name: "TargetDirectoryObjectIdentityUnavailableReason",
                table: "RootFolderRelocations");

            migrationBuilder.DropColumn(
                name: "TargetDirectoryObjectIdentityVersion",
                table: "RootFolderRelocations");

            migrationBuilder.DropColumn(
                name: "DirectoryObjectIdentity",
                table: "LibraryDirectoryOwnerships");

            migrationBuilder.DropColumn(
                name: "DirectoryObjectIdentityUnavailableReason",
                table: "LibraryDirectoryOwnerships");

            migrationBuilder.DropColumn(
                name: "DirectoryObjectIdentityVersion",
                table: "LibraryDirectoryOwnerships");

            migrationBuilder.DropColumn(
                name: "ManagedRootFolderId",
                table: "LibraryDirectoryOwnerships");
        }
    }
}
