using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTrackedSeries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TrackedSeries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    BookmarkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ProviderId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    MediaType = table.Column<int>(type: "INTEGER", nullable: false),
                    LatestKnownChapter = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastReleaseAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastChecked = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ChaptersRead = table.Column<double>(type: "REAL", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrackedSeries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrackedSeries_BookmarkNodes_BookmarkId",
                        column: x => x.BookmarkId,
                        principalTable: "BookmarkNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReleaseEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TrackedSeriesId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Chapter = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Volume = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReleaseEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReleaseEvents_TrackedSeries_TrackedSeriesId",
                        column: x => x.TrackedSeriesId,
                        principalTable: "TrackedSeries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReleaseEvents_TrackedSeriesId",
                table: "ReleaseEvents",
                column: "TrackedSeriesId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackedSeries_BookmarkId",
                table: "TrackedSeries",
                column: "BookmarkId");

            migrationBuilder.CreateIndex(
                name: "IX_TrackedSeries_Provider_ProviderId",
                table: "TrackedSeries",
                columns: new[] { "Provider", "ProviderId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReleaseEvents");

            migrationBuilder.DropTable(
                name: "TrackedSeries");
        }
    }
}
