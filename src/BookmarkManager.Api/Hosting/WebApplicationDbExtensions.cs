using BookmarkManager.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Hosting;

public static class WebApplicationDbExtensions
{
    public static async Task InitializeDatabaseAsync(this WebApplication app, CancellationToken ct = default)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connectionString = db.Database.GetConnectionString() ?? string.Empty;

        EnsureDirectoryForConnectionString(connectionString);
        await db.Database.MigrateAsync(ct);
        await EnableWalAsync(connectionString);
        await EnsureAppConfigAsync(db, ct);
    }

    private static void EnsureDirectoryForConnectionString(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;
        if (string.IsNullOrWhiteSpace(dataSource) || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static async Task EnableWalAsync(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)
            || connectionString.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase)
            || connectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task EnsureAppConfigAsync(AppDbContext db, CancellationToken ct)
    {
        var exists = await db.AppConfig.AnyAsync(c => c.Id == AppConfigConstants.SingletonId, ct);
        if (exists)
        {
            return;
        }

        db.AppConfig.Add(new AppConfig
        {
            Id = AppConfigConstants.SingletonId,
            ConfigVersion = 1,
            PollIntervalSeconds = AppConfigConstants.DefaultPollIntervalSeconds,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }
}
