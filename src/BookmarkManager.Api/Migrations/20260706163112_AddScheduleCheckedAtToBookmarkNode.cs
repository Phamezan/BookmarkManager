using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduleCheckedAtToBookmarkNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduleCheckedAt",
                table: "BookmarkNodes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScheduleCheckedAt",
                table: "BookmarkNodes");
        }
    }
}
