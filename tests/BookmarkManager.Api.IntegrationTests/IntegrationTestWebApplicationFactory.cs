using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class IntegrationTestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString = $"Data Source={Path.Combine(Path.GetTempPath(), $"bm-tests-{Guid.NewGuid():N}.db")}";
    private readonly string _tempDataDir = Path.Combine(Path.GetTempPath(), $"bm-data-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

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
