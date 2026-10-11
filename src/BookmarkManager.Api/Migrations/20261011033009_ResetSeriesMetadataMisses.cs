using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class ResetSeriesMetadataMisses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Misses recorded before the leading-article fix ("The Academy's ..." vs "Academy's ...")
            // would otherwise wait 30 days for a retry.
            migrationBuilder.Sql("DELETE FROM \"SeriesMetadata\" WHERE \"Found\" = 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
