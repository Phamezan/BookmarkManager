using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryCatalogEmbedding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "Embedding",
                table: "LibraryCatalogEntries",
                type: "BLOB",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EmbeddingSourceHash",
                table: "LibraryCatalogEntries",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Embedding",
                table: "LibraryCatalogEntries");

            migrationBuilder.DropColumn(
                name: "EmbeddingSourceHash",
                table: "LibraryCatalogEntries");
        }
    }
}
