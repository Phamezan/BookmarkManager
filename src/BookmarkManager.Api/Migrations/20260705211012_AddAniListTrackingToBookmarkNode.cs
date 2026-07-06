using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAniListTrackingToBookmarkNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AniListId",
                table: "BookmarkNodes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AniListMatchedAt",
                table: "BookmarkNodes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AniListId",
                table: "BookmarkNodes");

            migrationBuilder.DropColumn(
                name: "AniListMatchedAt",
                table: "BookmarkNodes");
        }
    }
}
