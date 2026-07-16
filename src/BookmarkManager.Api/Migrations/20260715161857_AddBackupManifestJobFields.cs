using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupManifestJobFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DurationMs",
                table: "BackupManifests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Error",
                table: "BackupManifests",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FolderCount",
                table: "BackupManifests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LibraryTitleCount",
                table: "BackupManifests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "BackupManifests",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "TagCount",
                table: "BackupManifests",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Trigger",
                table: "BackupManifests",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_BackupManifests_CreatedAt",
                table: "BackupManifests",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BackupManifests_CreatedAt",
                table: "BackupManifests");

            migrationBuilder.DropColumn(
                name: "DurationMs",
                table: "BackupManifests");

            migrationBuilder.DropColumn(
                name: "Error",
                table: "BackupManifests");

            migrationBuilder.DropColumn(
                name: "FolderCount",
                table: "BackupManifests");

            migrationBuilder.DropColumn(
                name: "LibraryTitleCount",
                table: "BackupManifests");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "BackupManifests");

            migrationBuilder.DropColumn(
                name: "TagCount",
                table: "BackupManifests");

            migrationBuilder.DropColumn(
                name: "Trigger",
                table: "BackupManifests");
        }
    }
}
