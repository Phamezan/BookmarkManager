using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDiscoverFeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DiscoverSeries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TitleKey = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Genres = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CoverMangaId = table.Column<int>(type: "INTEGER", nullable: false),
                    LatestChapterAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoverSeries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DiscoverChapters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeriesId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MangaId = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    ChapterNumber = table.Column<double>(type: "REAL", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SourceOrder = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoverChapters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoverChapters_DiscoverSeries_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "DiscoverSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DiscoverSourceEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SeriesId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    MangaId = table.Column<int>(type: "INTEGER", nullable: false),
                    LastListedRank = table.Column<int>(type: "INTEGER", nullable: true),
                    ChaptersFetchedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoverSourceEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DiscoverSourceEntries_DiscoverSeries_SeriesId",
                        column: x => x.SeriesId,
                        principalTable: "DiscoverSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoverChapters_MangaId_SourceOrder",
                table: "DiscoverChapters",
                columns: new[] { "MangaId", "SourceOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscoverChapters_SeriesId_UploadedAt",
                table: "DiscoverChapters",
                columns: new[] { "SeriesId", "UploadedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoverSeries_LatestChapterAt",
                table: "DiscoverSeries",
                column: "LatestChapterAt");

            migrationBuilder.CreateIndex(
                name: "IX_DiscoverSeries_TitleKey",
                table: "DiscoverSeries",
                column: "TitleKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscoverSourceEntries_MangaId",
                table: "DiscoverSourceEntries",
                column: "MangaId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscoverSourceEntries_SeriesId",
                table: "DiscoverSourceEntries",
                column: "SeriesId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscoverChapters");

            migrationBuilder.DropTable(
                name: "DiscoverSourceEntries");

            migrationBuilder.DropTable(
                name: "DiscoverSeries");
        }
    }
}
