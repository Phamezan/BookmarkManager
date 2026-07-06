using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddKitsuIdToBookmarkNode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "KitsuId",
                table: "BookmarkNodes",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "KitsuId",
                table: "BookmarkNodes");
        }
    }
}
