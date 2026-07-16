using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class IntegrationTestWebApplicationFactory : WebApplicationFactory<Program>
{
    // The live db lives in its own per-factory directory (not directly in the shared OS temp
    // folder) because restore-on-restart stages/reads files "next to" the live db
    // (restore-pending.db, force-repair-snapshot); a directory shared across factories would let
    // those marker files leak between unrelated tests.
    private readonly string _dbDir = Path.Combine(Path.GetTempPath(), $"bm-db-{Guid.NewGuid():N}");
    private readonly string _connectionString;
    private readonly string _tempDataDir = Path.Combine(Path.GetTempPath(), $"bm-data-{Guid.NewGuid():N}");
    private readonly string _backupDir = Path.Combine(Path.GetTempPath(), $"bm-backups-{Guid.NewGuid():N}");

    /// <summary>
    /// When set before the first <see cref="WebApplicationFactory{TEntryPoint}.CreateClient"/> call,
    /// overrides <c>Backup:MinFreeDiskBytes</c> (used to force preflight Failed manifests).
    /// </summary>
    public long? MinFreeDiskBytesOverride { get; set; }

    public IntegrationTestWebApplicationFactory()
    {
        Directory.CreateDirectory(_dbDir);
        _connectionString = $"Data Source={Path.Combine(_dbDir, "bookmarks.db")}";
    }

    public string ConnectionString => _connectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            var values = new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _connectionString,
                ["Backup:Directory"] = _backupDir,
                ["Backup:Enabled"] = "false",
                // The test host process is not restarted after a POST /restore like Docker/dotnet
                // run would be, so auto-stopping it here would just kill the host mid-test-suite.
                ["Backup:StopHostAfterRestore"] = "false"
            };

            if (MinFreeDiskBytesOverride is { } minFree)
            {
                values["Backup:MinFreeDiskBytes"] = minFree.ToString();
            }

            config.AddInMemoryCollection(values);
        });

        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<AppDbContext>));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connectionString));

            // Replace AiTaggingSettingsService with an isolated temp-dir instance
            // so integration tests never clobber C:\data\ai-tagging-settings.json.
            var settingsDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(AiTaggingSettingsService));
            if (settingsDescriptor is not null)
            {
                services.Remove(settingsDescriptor);
            }

            Directory.CreateDirectory(_tempDataDir);
            var settingsPath = Path.Combine(_tempDataDir, "ai-tagging-settings.json");
            services.AddSingleton<AiTaggingSettingsService>(new TestAiTaggingSettingsService(settingsPath));

            var sp = services.BuildServiceProvider();
            using var scope = sp.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.Database.EnsureDeleted();
        });
    }

    protected override void Dispose(bool disposing)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            if (File.Exists(_connectionString.Replace("Data Source=", string.Empty).Trim()))
            {
                SqliteConnection.ClearPool(connection);
            }
        }
        catch
        {
            // Best-effort cleanup; the temp file lives in the OS temp directory.
        }

        base.Dispose(disposing);
    }
}

internal sealed class TestAiTaggingSettingsService : AiTaggingSettingsService
{
    public TestAiTaggingSettingsService(string settingsPath)
        : base(Microsoft.Extensions.Logging.Abstractions.NullLogger<AiTaggingSettingsService>.Instance, settingsPath)
    {
    }
}
