using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddMangaCalendarMatchingToBookmarkNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastLibraryMatchAttemptAt",
                table: "BookmarkNodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LibraryMatchedAt",
                table: "BookmarkNodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LibraryProvider",
                table: "BookmarkNodes",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LibraryProviderId",
                table: "BookmarkNodes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastLibraryMatchAttemptAt",
                table: "BookmarkNodes");

            migrationBuilder.DropColumn(
                name: "LibraryMatchedAt",
                table: "BookmarkNodes");

            migrationBuilder.DropColumn(
                name: "LibraryProvider",
                table: "BookmarkNodes");

            migrationBuilder.DropColumn(
                name: "LibraryProviderId",
                table: "BookmarkNodes");
        }
    }
}
