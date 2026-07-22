using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BookmarkManager.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddLibraryCatalogSearchFts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Standalone (non-contentless) FTS5 index over the catalog's free-text columns, keyed by
            // EntryId (the catalog's Guid PK, stored as UNINDEXED text) rather than by matching FTS5's
            // own integer rowid to LibraryCatalogEntries' implicit rowid. A rowid=rowid join looked
            // simpler, but this app's SQLite backup path (BackupService.RunVacuumIntoAsync -> "VACUUM
            // INTO") is not guaranteed to renumber rowids identically across two independently-vacuumed
            // tables in the same file, which would silently desync a rowid join after a restore.
            // Keying by EntryId sidesteps that: sync triggers below delete-then-reinsert by EntryId, so
            // FTS5 is free to assign its own rowids and a restore can never observe a mismatch.
            migrationBuilder.Sql(
                """
                CREATE VIRTUAL TABLE LibraryCatalogSearch USING fts5(
                    EntryId UNINDEXED,
                    Title,
                    AlternateTitles,
                    Genres,
                    Synopsis,
                    tokenize = 'unicode61 remove_diacritics 2'
                );
                """);

            // Backfill from the ~45k existing rows.
            migrationBuilder.Sql(
                """
                INSERT INTO LibraryCatalogSearch (EntryId, Title, AlternateTitles, Genres, Synopsis)
                SELECT Id, Title, AlternateTitles, Genres, Synopsis FROM LibraryCatalogEntries;
                """);

            // Keep the FTS index in sync automatically - EF Core issues plain INSERT/UPDATE/DELETE
            // statements against LibraryCatalogEntries (catalog sync, embedding backfill, on-demand
            // enrichment), so ordinary AFTER triggers cover every write path with no app-code changes
            // and no drift risk. UPDATE is a delete+reinsert (not an in-place UPDATE) so the EntryId
            // lookup below always matches exactly one row, indexed-scan or not.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER LibraryCatalogEntries_fts_ai AFTER INSERT ON LibraryCatalogEntries BEGIN
                    INSERT INTO LibraryCatalogSearch (EntryId, Title, AlternateTitles, Genres, Synopsis)
                    VALUES (new.Id, new.Title, new.AlternateTitles, new.Genres, new.Synopsis);
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER LibraryCatalogEntries_fts_au AFTER UPDATE ON LibraryCatalogEntries BEGIN
                    DELETE FROM LibraryCatalogSearch WHERE EntryId = old.Id;
                    INSERT INTO LibraryCatalogSearch (EntryId, Title, AlternateTitles, Genres, Synopsis)
                    VALUES (new.Id, new.Title, new.AlternateTitles, new.Genres, new.Synopsis);
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER LibraryCatalogEntries_fts_ad AFTER DELETE ON LibraryCatalogEntries BEGIN
                    DELETE FROM LibraryCatalogSearch WHERE EntryId = old.Id;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS LibraryCatalogEntries_fts_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS LibraryCatalogEntries_fts_au;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS LibraryCatalogEntries_fts_ai;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS LibraryCatalogSearch;");
        }
    }
}
