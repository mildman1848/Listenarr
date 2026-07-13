using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Listenarr.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class HardenMoveExecutionAndScanHandoffs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceCaseSensitivity",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceCaseSensitivityMode",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceIdentityBoundary",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourcePathSyntax",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetCaseSensitivity",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetCaseSensitivityMode",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetIdentityBoundary",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetPathSyntax",
                table: "MoveJobs",
                type: "TEXT",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "History",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MoveJobCreatedDirectories",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MoveJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoveJobCreatedDirectories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MoveJobCreatedDirectories_MoveJobs_MoveJobId",
                        column: x => x.MoveJobId,
                        principalTable: "MoveJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "MoveScanHandoffs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    MoveJobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AudiobookId = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetPath = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    AttemptGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaseOwner = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    LeaseGeneration = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaseExpiresAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextAttemptAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ActiveScanJobId = table.Column<Guid>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MoveScanHandoffs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MoveScanHandoffs_MoveJobs_MoveJobId",
                        column: x => x.MoveJobId,
                        principalTable: "MoveJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_History_IdempotencyKey",
                table: "History",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MoveJobCreatedDirectories_MoveJobId_Path",
                table: "MoveJobCreatedDirectories",
                columns: new[] { "MoveJobId", "Path" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoveScanHandoffs_MoveJobId",
                table: "MoveScanHandoffs",
                column: "MoveJobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MoveScanHandoffs_Status_NextAttemptAt_LeaseExpiresAt",
                table: "MoveScanHandoffs",
                columns: new[] { "Status", "NextAttemptAt", "LeaseExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MoveJobCreatedDirectories");

            migrationBuilder.DropTable(
                name: "MoveScanHandoffs");

            migrationBuilder.DropIndex(
                name: "IX_History_IdempotencyKey",
                table: "History");

            migrationBuilder.DropColumn(
                name: "SourceCaseSensitivity",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "SourceCaseSensitivityMode",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "SourceIdentityBoundary",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "SourcePathSyntax",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "TargetCaseSensitivity",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "TargetCaseSensitivityMode",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "TargetIdentityBoundary",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "TargetPathSyntax",
                table: "MoveJobs");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "History");
        }
    }
}
