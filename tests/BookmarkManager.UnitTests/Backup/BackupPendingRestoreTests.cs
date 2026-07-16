using BookmarkManager.Api.Services.Backup;
using Microsoft.Data.Sqlite;

namespace BookmarkManager.UnitTests.Backup;

public sealed class BackupPendingRestoreTests
{
    [Fact]
    public void StageThenApply_SwapsLiveDatabaseWithSnapshotContents()
    {
        var tempDir = CreateTempDir();
        try
        {
            var liveDbPath = Path.Combine(tempDir, "bookmarks.db");
            var connectionString = $"Data Source={liveDbPath}";
            CreateMarkerDatabase(liveDbPath, "live-marker");

            var snapshotPath = Path.Combine(tempDir, "snapshot.db");
            CreateMarkerDatabase(snapshotPath, "snapshot-marker");

            BackupPendingRestore.StagePendingRestore(snapshotPath, connectionString);
            var pendingPath = BackupPendingRestore.GetPendingRestorePath(connectionString);
            Assert.True(File.Exists(pendingPath));

            var applied = BackupPendingRestore.ApplyPendingRestoreIfAny(connectionString);

            Assert.True(applied);
            Assert.False(File.Exists(pendingPath));
            Assert.Equal("snapshot-marker", ReadMarker(liveDbPath));

            var markerPath = BackupPendingRestore.GetForceRepairMarkerPath(connectionString);
            Assert.True(File.Exists(markerPath));
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public void ApplyPendingRestoreIfAny_NoOpsWhenPendingMissing()
    {
        var tempDir = CreateTempDir();
        try
        {
            var liveDbPath = Path.Combine(tempDir, "bookmarks.db");
            var connectionString = $"Data Source={liveDbPath}";
            CreateMarkerDatabase(liveDbPath, "live-marker");

            var applied = BackupPendingRestore.ApplyPendingRestoreIfAny(connectionString);

            Assert.False(applied);
            Assert.Equal("live-marker", ReadMarker(liveDbPath));
            Assert.False(File.Exists(BackupPendingRestore.GetForceRepairMarkerPath(connectionString)));
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task VerifySnapshotAsync_RejectsCorruptFile()
    {
        var tempDir = CreateTempDir();
        try
        {
            var corruptPath = Path.Combine(tempDir, "corrupt.db");
            await File.WriteAllBytesAsync(corruptPath, [1, 2, 3, 4, 5, 6, 7, 8]);

            await Assert.ThrowsAsync<BackupRestoreException>(
                () => BackupPendingRestore.VerifySnapshotAsync(corruptPath, CancellationToken.None));
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task VerifySnapshotAsync_RejectsMissingFile()
    {
        var tempDir = CreateTempDir();
        try
        {
            var missingPath = Path.Combine(tempDir, "does-not-exist.db");

            await Assert.ThrowsAsync<BackupRestoreException>(
                () => BackupPendingRestore.VerifySnapshotAsync(missingPath, CancellationToken.None));
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task VerifySnapshotAsync_PassesForValidDatabaseWithBookmarkNodesTable()
    {
        var tempDir = CreateTempDir();
        try
        {
            var dbPath = Path.Combine(tempDir, "valid.db");
            CreateMarkerDatabase(dbPath, "ok");

            await BackupPendingRestore.VerifySnapshotAsync(dbPath, CancellationToken.None);
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    [Fact]
    public async Task VerifySnapshotAsync_RejectsDatabaseMissingBookmarkNodesTable()
    {
        var tempDir = CreateTempDir();
        try
        {
            var dbPath = Path.Combine(tempDir, "no-bookmarks.db");
            using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE SomethingElse (Id INTEGER PRIMARY KEY);";
                command.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            await Assert.ThrowsAsync<BackupRestoreException>(
                () => BackupPendingRestore.VerifySnapshotAsync(dbPath, CancellationToken.None));
        }
        finally
        {
            CleanupTempDir(tempDir);
        }
    }

    private static string CreateTempDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"bm-restore-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
    }

    private static void CleanupTempDir(string tempDir)
    {
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; OS temp directory gets swept independently.
        }
    }

    private static void CreateMarkerDatabase(string path, string markerValue)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();

        using var createCommand = connection.CreateCommand();
        createCommand.CommandText =
            "CREATE TABLE BookmarkNodes (Id TEXT PRIMARY KEY); CREATE TABLE Marker (Value TEXT NOT NULL);";
        createCommand.ExecuteNonQuery();

        using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText = "INSERT INTO Marker (Value) VALUES ($value);";
        insertCommand.Parameters.AddWithValue("$value", markerValue);
        insertCommand.ExecuteNonQuery();
    }

    private static string ReadMarker(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Marker LIMIT 1;";
        return (string)command.ExecuteScalar()!;
    }
}
