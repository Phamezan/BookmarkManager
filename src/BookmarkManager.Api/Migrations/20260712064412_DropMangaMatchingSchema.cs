using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class DropMangaMatchingSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MangaReleaseCaches");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
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

            migrationBuilder.CreateTable(
                name: "MangaReleaseCaches",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    CoverImageUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastReleaseAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LatestChapter = table.Column<string>(type: "TEXT", nullable: true),
                    LatestVolume = table.Column<string>(type: "TEXT", nullable: true),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MangaReleaseCaches", x => new { x.Provider, x.ProviderId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_MangaReleaseCaches_ExpiresAtUtc",
                table: "MangaReleaseCaches",
                column: "ExpiresAtUtc");
        }
    }
}
