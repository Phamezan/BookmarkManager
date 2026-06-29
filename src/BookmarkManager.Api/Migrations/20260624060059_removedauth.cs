using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class removedauth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BrowserNodeId",
                table: "BookmarkNodes",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentBrowserNodeId",
                table: "BookmarkNodes",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BookmarkNodes_BrowserNodeId",
                table: "BookmarkNodes",
                column: "BrowserNodeId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BookmarkNodes_BrowserNodeId",
                table: "BookmarkNodes");

            migrationBuilder.DropColumn(
                name: "BrowserNodeId",
                table: "BookmarkNodes");

            migrationBuilder.DropColumn(
                name: "ParentBrowserNodeId",
                table: "BookmarkNodes");
        }
    }
}
