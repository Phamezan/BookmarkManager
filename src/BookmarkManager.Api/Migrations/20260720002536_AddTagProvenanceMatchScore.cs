using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTagProvenanceMatchScore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "MatchScore",
                table: "TagProvenances",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MatchedTitle",
                table: "TagProvenances",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MatchScore",
                table: "TagProvenances");

            migrationBuilder.DropColumn(
                name: "MatchedTitle",
                table: "TagProvenances");
        }
    }
}
