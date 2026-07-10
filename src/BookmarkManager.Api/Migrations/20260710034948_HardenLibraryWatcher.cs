using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class HardenLibraryWatcher : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ConsecutiveFailureCount",
                table: "TrackedSeries",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "LastCheckError",
                table: "TrackedSeries",
                type: "TEXT",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextCheckAt",
                table: "TrackedSeries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReleaseWatcherIntervalHours",
                table: "AppConfig",
                type: "INTEGER",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.CreateIndex(
                name: "IX_TrackedSeries_NextCheckAt",
                table: "TrackedSeries",
                column: "NextCheckAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TrackedSeries_NextCheckAt",
                table: "TrackedSeries");

            migrationBuilder.DropColumn(
                name: "ConsecutiveFailureCount",
                table: "TrackedSeries");

            migrationBuilder.DropColumn(
                name: "LastCheckError",
                table: "TrackedSeries");

            migrationBuilder.DropColumn(
                name: "NextCheckAt",
                table: "TrackedSeries");

            migrationBuilder.DropColumn(
                name: "ReleaseWatcherIntervalHours",
                table: "AppConfig");
        }
    }
}
