using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddNovelLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NovelLibraryEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    NovelPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    CoverUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LastChapterPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    LastChapterName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    LastChapterIndex = table.Column<int>(type: "INTEGER", nullable: true),
                    ChapterCount = table.Column<int>(type: "INTEGER", nullable: true),
                    ScrollPercent = table.Column<double>(type: "REAL", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastReadAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NovelLibraryEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_NovelLibraryEntries_LastReadAt",
                table: "NovelLibraryEntries",
                column: "LastReadAt");

            migrationBuilder.CreateIndex(
                name: "IX_NovelLibraryEntries_Source_NovelPath",
                table: "NovelLibraryEntries",
                columns: new[] { "Source", "NovelPath" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NovelLibraryEntries");
        }
    }
}
