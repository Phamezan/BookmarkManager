using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class RemoveKitsuIdFromBookmarkNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KitsuId",
                table: "BookmarkNodes");

            // Dropping KitsuId leaves every previously Kitsu-only match as unmatched (AniListId is
            // null). Clear their stale media status and match-attempt cooldown so the next auto-match
            // re-resolves them against AniList promptly instead of waiting out the cooldown.
            migrationBuilder.Sql(
                "UPDATE BookmarkNodes SET MediaStatus = NULL, LastMatchAttemptAt = NULL WHERE AniListId IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KitsuId",
                table: "BookmarkNodes",
                type: "TEXT",
                nullable: true);
        }
    }
}
