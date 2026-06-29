using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class RepairMinValueUpdatedTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Repair existing BookmarkNodes rows whose UpdatedAt was persisted as
            // DateTime.MinValue (0001-01-01) by earlier snapshot ingestion code that
            // did not fall back to the snapshot's CapturedAt. We rewrite only those
            // rows to a sane fixed UTC fallback (2026-01-01T00:00:00Z, matching the
            // extension rollout window). Rows with a real timestamp are untouched.
            // The LIKE pattern tolerates both EF Core's default SQLite TEXT format
            // ("0001-01-01 00:00:00.0000000") and ISO-8601 variants ("0001-01-01T...").
            // Down is a no-op: the original default value is unrecoverable.
            migrationBuilder.Sql(
                "UPDATE \"BookmarkNodes\" " +
                "SET \"UpdatedAt\" = '2026-01-01 00:00:00.0000000' " +
                "WHERE \"UpdatedAt\" LIKE '0001-01-01%';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: the original DateTime.MinValue values cannot be meaningfully
            // restored, and reverting would reintroduce the display bug.
        }
    }
}
