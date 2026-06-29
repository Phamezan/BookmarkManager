using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using BookmarkManager.Api.Data;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class IntegrationTestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString = $"Data Source={Path.Combine(Path.GetTempPath(), $"bm-tests-{Guid.NewGuid():N}.db")}";

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
