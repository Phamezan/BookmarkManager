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

            // EntryId is UNINDEXED (FTS5 doesn't build a B-tree over it - only over the tokenized text
            // columns), so "WHERE EntryId = ?" against LibraryCatalogSearch is a full scan of the FTS
            // table. That's fine for one-off diagnostics but not for the per-row sync triggers below:
            // LibraryEmbeddingBackfillService re-embeds the whole catalog on a model change (we just
            // switched to bge-base-en-v1.5), which is ~45k UPDATEs firing that scan against a ~45k-row
            // FTS table. This ordinary rowid table gives an indexed (PRIMARY KEY = clustered B-tree,
            // WITHOUT ROWID) EntryId -> FTS-rowid lookup, so a trigger firing can find "which FTS5 row is
            // this catalog row" in O(log n) and then touch that row directly by rowid - the FTS5-native,
            // O(document size) way to update/delete a single indexed document - instead of scanning every
            // row in the FTS table.
            migrationBuilder.Sql(
                """
                CREATE TABLE LibraryCatalogSearchMap (
                    EntryId TEXT PRIMARY KEY,
                    FtsRowid INTEGER NOT NULL
                ) WITHOUT ROWID;
                """);

            // Backfill from the ~45k existing rows. Both SELECTs use the identical deterministic
            // ROW_NUMBER() OVER (ORDER BY rowid) so the FTS5 rowid assigned to a row and the value
            // recorded for it in the map table always agree - relying on SQLite's default insert order
            // matching across two separate statements would be implementation-defined, not guaranteed.
            migrationBuilder.Sql(
                """
                INSERT INTO LibraryCatalogSearch (rowid, EntryId, Title, AlternateTitles, Genres, Synopsis)
                SELECT ROW_NUMBER() OVER (ORDER BY rowid), Id, Title, AlternateTitles, Genres, Synopsis
                FROM LibraryCatalogEntries;
                """);

            migrationBuilder.Sql(
                """
                INSERT INTO LibraryCatalogSearchMap (EntryId, FtsRowid)
                SELECT Id, ROW_NUMBER() OVER (ORDER BY rowid)
                FROM LibraryCatalogEntries;
                """);

            // Keep the FTS index in sync automatically - EF Core issues plain INSERT/UPDATE/DELETE
            // statements against LibraryCatalogEntries (catalog sync, embedding backfill, on-demand
            // enrichment), so ordinary AFTER triggers cover every write path with no app-code changes
            // and no drift risk.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER LibraryCatalogEntries_fts_ai AFTER INSERT ON LibraryCatalogEntries BEGIN
                    INSERT INTO LibraryCatalogSearch (EntryId, Title, AlternateTitles, Genres, Synopsis)
                    VALUES (new.Id, new.Title, new.AlternateTitles, new.Genres, new.Synopsis);
                    INSERT INTO LibraryCatalogSearchMap (EntryId, FtsRowid) VALUES (new.Id, last_insert_rowid());
                END;
                """);

            // Scoped to "OF Title, AlternateTitles, Genres, Synopsis" - SQLite only fires an "UPDATE OF
            // <columns>" trigger when the UPDATE statement's SET clause actually names one of those
            // columns. EF Core only puts changed properties in SET, so an embedding-only write (the
            // backfill worker's normal path) never touches this trigger at all - not "cheap", genuinely
            // zero rows visited in LibraryCatalogSearch/-Map. A real title/synopsis/genre change still
            // costs one indexed map lookup + one FTS5 rowid-keyed UPDATE (reindexes just that document),
            // not a scan - so this scales fine even when catalog-sync synopsis enrichment touches
            // thousands of rows in a pass.
            migrationBuilder.Sql(
                """
                CREATE TRIGGER LibraryCatalogEntries_fts_au AFTER UPDATE OF Title, AlternateTitles, Genres, Synopsis ON LibraryCatalogEntries BEGIN
                    UPDATE LibraryCatalogSearch
                       SET Title = new.Title, AlternateTitles = new.AlternateTitles, Genres = new.Genres, Synopsis = new.Synopsis
                     WHERE rowid = (SELECT FtsRowid FROM LibraryCatalogSearchMap WHERE EntryId = new.Id);
                END;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER LibraryCatalogEntries_fts_ad AFTER DELETE ON LibraryCatalogEntries BEGIN
                    DELETE FROM LibraryCatalogSearch WHERE rowid = (SELECT FtsRowid FROM LibraryCatalogSearchMap WHERE EntryId = old.Id);
                    DELETE FROM LibraryCatalogSearchMap WHERE EntryId = old.Id;
                END;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS LibraryCatalogEntries_fts_ad;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS LibraryCatalogEntries_fts_au;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS LibraryCatalogEntries_fts_ai;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS LibraryCatalogSearchMap;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS LibraryCatalogSearch;");
        }
    }
}
