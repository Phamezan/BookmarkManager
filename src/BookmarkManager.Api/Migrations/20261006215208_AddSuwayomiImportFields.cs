using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSuwayomiImportFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSuwayomi",
                table: "UrlMigrationProposals",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MatchedTitle",
                table: "UrlMigrationProposals",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceLatestChapter",
                table: "UrlMigrationProposals",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceName",
                table: "UrlMigrationProposals",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SuwayomiMangaId",
                table: "UrlMigrationProposals",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceUrl",
                table: "BookmarkNodes",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SuwayomiMangaId",
                table: "BookmarkNodes",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsSuwayomi",
                table: "UrlMigrationProposals");

            migrationBuilder.DropColumn(
                name: "MatchedTitle",
                table: "UrlMigrationProposals");

            migrationBuilder.DropColumn(
                name: "SourceLatestChapter",
                table: "UrlMigrationProposals");

            migrationBuilder.DropColumn(
                name: "SourceName",
                table: "UrlMigrationProposals");

            migrationBuilder.DropColumn(
                name: "SuwayomiMangaId",
                table: "UrlMigrationProposals");

            migrationBuilder.DropColumn(
                name: "SourceUrl",
                table: "BookmarkNodes");

            migrationBuilder.DropColumn(
                name: "SuwayomiMangaId",
                table: "BookmarkNodes");
        }
    }
}
