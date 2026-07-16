using Microsoft.Data.Sqlite;

namespace BookmarkManager.Api.Services.Backup;

/// <summary>
/// Implements the "restore-on-restart" strategy: a verified snapshot is staged next to the
/// live database as <see cref="PendingFileName"/>, and applied the next time the process starts,
/// before EF Core opens any connection or runs migrations. This avoids swapping the live SQLite
/// file out from under an open EF Core connection pool.
/// </summary>
public static class BackupPendingRestore
{
    public const string PendingFileName = "restore-pending.db";
    public const string ForceRepairSnapshotMarker = "force-repair-snapshot";

    public static string GetLiveDatabasePath(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        return Path.GetFullPath(builder.DataSource);
    }

    public static string GetPendingRestorePath(string connectionString)
        => Path.Combine(GetLiveDatabaseDirectory(connectionString), PendingFileName);

    public static string GetForceRepairMarkerPath(string connectionString)
        => Path.Combine(GetLiveDatabaseDirectory(connectionString), ForceRepairSnapshotMarker);

    public static async Task VerifySnapshotAsync(string snapshotPath, CancellationToken ct)
    {
        if (!File.Exists(snapshotPath))
        {
            throw new BackupRestoreException("Snapshot file does not exist.");
        }

        var readOnlyConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = snapshotPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        try
        {
            await using var connection = new SqliteConnection(readOnlyConnectionString);
            await connection.OpenAsync(ct);

            await using var integrityCommand = connection.CreateCommand();
            integrityCommand.CommandText = "PRAGMA integrity_check;";
            var integrityResult = await integrityCommand.ExecuteScalarAsync(ct);
            if (integrityResult is not string integrityText
                || !integrityText.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new BackupRestoreException(
                    $"Snapshot failed integrity check: {integrityResult}.");
            }

            await using var tableCommand = connection.CreateCommand();
            tableCommand.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'BookmarkNodes';";
            var tableCount = Convert.ToInt64(await tableCommand.ExecuteScalarAsync(ct));
            if (tableCount == 0)
            {
                throw new BackupRestoreException("Snapshot is missing the BookmarkNodes table.");
            }
        }
        catch (SqliteException ex)
        {
            throw new BackupRestoreException("Snapshot could not be opened for verification.", ex);
        }
        finally
        {
            SqliteConnection.ClearPool(new SqliteConnection(readOnlyConnectionString));
        }
    }

    public static void StagePendingRestore(string snapshotFullPath, string connectionString)
    {
        var pendingPath = GetPendingRestorePath(connectionString);
        var directory = Path.GetDirectoryName(pendingPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(snapshotFullPath, pendingPath, overwrite: true);
    }

    /// <summary>
    /// Must run before any EF Core DbContext is created or migrated. Safe to call when there is
    /// no pending restore staged (no-op).
    /// </summary>
    public static bool ApplyPendingRestoreIfAny(string connectionString)
    {
        var pendingPath = GetPendingRestorePath(connectionString);
        if (!File.Exists(pendingPath))
        {
            return false;
        }

        var liveDbPath = GetLiveDatabasePath(connectionString);

        // Ensure no pooled connection still holds a handle to the live file before we overwrite it.
        SqliteConnection.ClearAllPools();

        DeleteFileIfExists(liveDbPath + "-wal");
        DeleteFileIfExists(liveDbPath + "-shm");

        var directory = Path.GetDirectoryName(liveDbPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(pendingPath, liveDbPath, overwrite: true);
        DeleteFileIfExists(pendingPath);

        File.WriteAllText(GetForceRepairMarkerPath(connectionString), DateTime.UtcNow.ToString("O"));

        return true;
    }

    private static string GetLiveDatabaseDirectory(string connectionString)
    {
        var liveDbPath = GetLiveDatabasePath(connectionString);
        var directory = Path.GetDirectoryName(liveDbPath);
        return string.IsNullOrWhiteSpace(directory) ? AppDomain.CurrentDomain.BaseDirectory : directory;
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
