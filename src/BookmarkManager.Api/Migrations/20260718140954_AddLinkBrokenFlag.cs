using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLinkBrokenFlag : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsLinkBroken",
                table: "BookmarkNodes",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "LinkCheckedAt",
                table: "BookmarkNodes",
                type: "TEXT",
                nullable: true);

            // Backfill: bookmarks the link checker previously MOVED into the "Broken Links"
            // folder keep their broken flag so the URL migrator still sees them now that
            // detection is report-only (no folder moves). Users restore them manually.
            migrationBuilder.Sql("""
                UPDATE BookmarkNodes
                SET IsLinkBroken = 1
                WHERE ParentId IN (
                    SELECT Id FROM BookmarkNodes
                    WHERE Type = 1 AND Title = 'Broken Links' AND IsDeleted = 0);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsLinkBroken",
                table: "BookmarkNodes");

            migrationBuilder.DropColumn(
                name: "LinkCheckedAt",
                table: "BookmarkNodes");
        }
    }
}
