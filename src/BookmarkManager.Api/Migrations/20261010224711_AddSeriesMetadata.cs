using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSeriesMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SeriesMetadata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    NormalizedKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    AnilistId = table.Column<int>(type: "INTEGER", nullable: true),
                    CountryOfOrigin = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    TitlesJson = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    Found = table.Column<bool>(type: "INTEGER", nullable: false),
                    LookedUpAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesMetadata", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SeriesMetadata_NormalizedKey",
                table: "SeriesMetadata",
                column: "NormalizedKey",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SeriesMetadata");
        }
    }
}
