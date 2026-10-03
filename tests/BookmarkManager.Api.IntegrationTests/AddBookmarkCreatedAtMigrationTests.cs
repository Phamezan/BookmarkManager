using System;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

/// <summary>
/// Verifies the <c>AddBookmarkCreatedAt</c> migration backfills existing rows with
/// <c>CreatedAt = UpdatedAt</c>. Migrates a fresh in-memory database to the migration just
/// before the new one, inserts a legacy row without CreatedAt, then applies the new migration.
/// </summary>
public sealed class AddBookmarkCreatedAtMigrationTests
{
    private const string PreviousMigration = "20260722153650_AddLibraryCatalogSearchFts";

    [Fact]
    public async Task Backfill_CopiesUpdatedAtIntoCreatedAt_ForExistingRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);

        var id = Guid.NewGuid();
        const string updatedAt = "2025-01-02 03:04:05.0000000";

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText =
                "INSERT INTO \"BookmarkNodes\" " +
                "(\"Id\",\"IsDeleted\",\"IsFavorite\",\"IsLinkBroken\",\"IsProtected\",\"Position\",\"SyncState\",\"Title\",\"Type\",\"UpdatedAt\",\"Version\") " +
                "VALUES ($id,'0','0','0','0','0','1','Legacy','0',$updatedAt,'1');";
            insert.Parameters.AddWithValue("$id", id.ToString("D"));
            insert.Parameters.AddWithValue("$updatedAt", updatedAt);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync());
        }

        await migrator.MigrateAsync();

        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT \"CreatedAt\", \"UpdatedAt\" FROM \"BookmarkNodes\" WHERE \"Id\" = $id;";
        select.Parameters.AddWithValue("$id", id.ToString("D"));

        await using var reader = await select.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var createdAt = reader.GetString(0);
        var persistedUpdatedAt = reader.GetString(1);

        Assert.Equal(persistedUpdatedAt, createdAt);
        Assert.Equal(updatedAt, createdAt);
    }
}
