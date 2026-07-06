using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAnimeScheduleCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AnimeScheduleCaches",
                columns: table => new
                {
                    AniListId = table.Column<int>(type: "INTEGER", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ResolvedAniListId = table.Column<int>(type: "INTEGER", nullable: true),
                    ResolvedTitle = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ResolvedCoverImageUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    EpisodesJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AnimeScheduleCaches", x => x.AniListId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AnimeScheduleCaches_ExpiresAtUtc",
                table: "AnimeScheduleCaches",
                column: "ExpiresAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AnimeScheduleCaches");
        }
    }
}
