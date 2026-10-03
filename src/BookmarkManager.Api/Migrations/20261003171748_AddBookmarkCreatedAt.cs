using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBookmarkCreatedAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "BookmarkNodes",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Backfill: existing rows predate CreatedAt, so the closest honest value is
            // their UpdatedAt (which, for rows never edited since creation, is the add time).
            // Done after AddColumn so every pre-existing row is rewritten from the default.
            migrationBuilder.Sql(
                "UPDATE \"BookmarkNodes\" SET \"CreatedAt\" = \"UpdatedAt\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "BookmarkNodes");
        }
    }
}
