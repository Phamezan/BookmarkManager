using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_BookmarkNodes_Tags",
                table: "BookmarkNodes",
                column: "Tags");

            migrationBuilder.CreateIndex(
                name: "IX_BookmarkNodes_Title",
                table: "BookmarkNodes",
                column: "Title");

            migrationBuilder.CreateIndex(
                name: "IX_BookmarkNodes_Url",
                table: "BookmarkNodes",
                column: "Url");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BookmarkNodes_Tags",
                table: "BookmarkNodes");

            migrationBuilder.DropIndex(
                name: "IX_BookmarkNodes_Title",
                table: "BookmarkNodes");

            migrationBuilder.DropIndex(
                name: "IX_BookmarkNodes_Url",
                table: "BookmarkNodes");
        }
    }
}
