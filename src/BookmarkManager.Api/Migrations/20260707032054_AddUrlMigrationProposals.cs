using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddUrlMigrationProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PreviousUrl",
                table: "BookmarkNodes",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "UrlMigrationProposals",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    BookmarkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeadHost = table.Column<string>(type: "TEXT", maxLength: 255, nullable: false),
                    OldUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: false),
                    ProposedUrl = table.Column<string>(type: "TEXT", maxLength: 2048, nullable: true),
                    ProposedHost = table.Column<string>(type: "TEXT", maxLength: 255, nullable: true),
                    SeriesName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    ChapterNumber = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Confidence = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DecidedAt = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UrlMigrationProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UrlMigrationProposals_BookmarkNodes_BookmarkId",
                        column: x => x.BookmarkId,
                        principalTable: "BookmarkNodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UrlMigrationProposals_BookmarkId",
                table: "UrlMigrationProposals",
                column: "BookmarkId");

            migrationBuilder.CreateIndex(
                name: "IX_UrlMigrationProposals_RunId",
                table: "UrlMigrationProposals",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_UrlMigrationProposals_Status",
                table: "UrlMigrationProposals",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UrlMigrationProposals");

            migrationBuilder.DropColumn(
                name: "PreviousUrl",
                table: "BookmarkNodes");
        }
    }
}
