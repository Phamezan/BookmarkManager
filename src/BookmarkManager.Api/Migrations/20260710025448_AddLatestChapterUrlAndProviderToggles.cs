using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLatestChapterUrlAndProviderToggles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LatestChapterUrl",
                table: "TrackedSeries",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Url",
                table: "ReleaseEvents",
                type: "TEXT",
                maxLength: 2048,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DisabledProviders",
                table: "AppConfig",
                type: "TEXT",
                maxLength: 2048,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LatestChapterUrl",
                table: "TrackedSeries");

            migrationBuilder.DropColumn(
                name: "Url",
                table: "ReleaseEvents");

            migrationBuilder.DropColumn(
                name: "DisabledProviders",
                table: "AppConfig");
        }
    }
}
