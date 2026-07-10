using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LibraryCatalogEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    AlternateTitles = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Authors = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    CoverImageUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Synopsis = table.Column<string>(type: "TEXT", nullable: true),
                    Genres = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    Rating = table.Column<double>(type: "REAL", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LatestChapter = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LatestVolume = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastReleaseAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    PopularityRank = table.Column<int>(type: "INTEGER", nullable: true),
                    FirstImportedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastRefreshedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryCatalogEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LibraryCatalogSyncQueue",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    MediaTypeQuery = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ContinuationToken = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    RemainingPages = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LibraryCatalogSyncQueue", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LibraryCatalogEntries_MediaType",
                table: "LibraryCatalogEntries",
                column: "MediaType");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryCatalogEntries_PopularityRank",
                table: "LibraryCatalogEntries",
                column: "PopularityRank");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryCatalogEntries_Provider_ProviderId",
                table: "LibraryCatalogEntries",
                columns: new[] { "Provider", "ProviderId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LibraryCatalogSyncQueue_NextAttemptAt",
                table: "LibraryCatalogSyncQueue",
                column: "NextAttemptAt");

            migrationBuilder.CreateIndex(
                name: "IX_LibraryCatalogSyncQueue_Provider_Status",
                table: "LibraryCatalogSyncQueue",
                columns: new[] { "Provider", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LibraryCatalogEntries");

            migrationBuilder.DropTable(
                name: "LibraryCatalogSyncQueue");
        }
    }
}
