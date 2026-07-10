using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class LibraryTrackingIntegrationTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly LibraryTrackingTestFactory _factory = new();

    [Fact]
    public async Task Track_CreatesBookmarkAndTrackedSeries()
    {
        using var client = _factory.CreateClient();
        
        var request = new TrackLibraryEntryRequest
        {
            ParentId = Guid.Empty,
            Provider = "MangaDex",
            ProviderId = "series-123",
            Title = "Test Manga",
            MediaType = LibraryMediaType.Manga,
            CoverImageUrl = "https://example.com/cover.jpg",
            LatestChapter = "5",
            SourceUrl = "https://example.com/source",
            Genres = ["Action", "Adventure"],
            ChaptersRead = 2,
            Status = "Reading"
        };

        using var response = await client.PostAsJsonAsync("/api/library/track", request, JsonOptions);
        response.EnsureSuccessStatusCode();

        var bookmark = await response.Content.ReadFromJsonAsync<BookmarkNodeDto>(JsonOptions);
        Assert.NotNull(bookmark);
        Assert.Equal("Test Manga", bookmark!.Title);
        Assert.Equal("https://example.com/source", bookmark.Url);
        Assert.Equal("Manga", bookmark.Metadata?.Category);
        Assert.Contains("Action", bookmark.Metadata?.Tags ?? []);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var ts = await db.TrackedSeries.Include(x => x.Bookmark).FirstOrDefaultAsync(x => x.BookmarkId == bookmark.Id);
        Assert.NotNull(ts);
        Assert.Equal("MangaDex", ts!.Provider);
        Assert.Equal("series-123", ts.ProviderId);
        Assert.Equal(2, ts.ChaptersRead);
        Assert.Equal("5", ts.LatestKnownChapter);
    }

    [Fact]
    public async Task Track_DuplicateGuard_ReturnsExistingBookmark()
    {
        using var client = _factory.CreateClient();
        
        var request = new TrackLibraryEntryRequest
        {
            ParentId = Guid.Empty,
            Provider = "MangaDex",
            ProviderId = "duplicate-123",
            Title = "Duplicate Title",
            MediaType = LibraryMediaType.Manga,
            SourceUrl = "https://example.com/source",
            Status = "Reading"
        };

        using var response1 = await client.PostAsJsonAsync("/api/library/track", request, JsonOptions);
        response1.EnsureSuccessStatusCode();
        var b1 = await response1.Content.ReadFromJsonAsync<BookmarkNodeDto>(JsonOptions);

        using var response2 = await client.PostAsJsonAsync("/api/library/track", request, JsonOptions);
        response2.EnsureSuccessStatusCode();
        var b2 = await response2.Content.ReadFromJsonAsync<BookmarkNodeDto>(JsonOptions);

        Assert.NotNull(b1);
        Assert.NotNull(b2);
        Assert.Equal(b1!.Id, b2!.Id);
    }

    [Fact]
    public async Task Track_AfterSoftDelete_RestoresExistingTrackedBookmark()
    {
        using var client = _factory.CreateClient();
        var request = new TrackLibraryEntryRequest
        {
            ParentId = Guid.Empty,
            Provider = "MangaDex",
            ProviderId = "restore-123",
            Title = "Restore Title",
            MediaType = LibraryMediaType.Manga,
            SourceUrl = "https://example.com/restore"
        };

        using var firstResponse = await client.PostAsJsonAsync("/api/library/track", request, JsonOptions);
        firstResponse.EnsureSuccessStatusCode();
        var firstBookmark = await firstResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(JsonOptions);
        Assert.NotNull(firstBookmark);

        using var deleteResponse = await client.DeleteAsync($"/api/bookmarks/{firstBookmark!.Id}");
        deleteResponse.EnsureSuccessStatusCode();

        using var retrackResponse = await client.PostAsJsonAsync("/api/library/track", request, JsonOptions);
        retrackResponse.EnsureSuccessStatusCode();
        var restoredBookmark = await retrackResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(JsonOptions);

        Assert.NotNull(restoredBookmark);
        Assert.Equal(firstBookmark.Id, restoredBookmark!.Id);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storedBookmark = await db.BookmarkNodes.SingleAsync(item => item.Id == firstBookmark.Id);
        Assert.False(storedBookmark.IsDeleted);
        Assert.Equal(
            1,
            await db.TrackedSeries.CountAsync(item =>
                item.Provider == request.Provider && item.ProviderId == request.ProviderId));
        Assert.Contains(
            await db.ExtensionCommands.Where(item => item.BookmarkId == firstBookmark.Id).ToListAsync(),
            command => command.CommandType == "Restore");
    }

    [Fact]
    public async Task Track_RollsBackBookmarkAndCommand_WhenTrackedSeriesInsertFails()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER FailTrackedSeriesInsert
            BEFORE INSERT ON TrackedSeries
            BEGIN
                SELECT RAISE(ABORT, 'forced tracked-series failure');
            END;
            """);

        try
        {
            using var client = _factory.CreateClient();
            var request = new TrackLibraryEntryRequest
            {
                ParentId = Guid.Empty,
                Provider = "MangaDex",
                ProviderId = "rollback-123",
                Title = "Rollback Title",
                MediaType = LibraryMediaType.Manga,
                SourceUrl = "https://example.com/rollback"
            };

            using var response = await client.PostAsJsonAsync("/api/library/track", request, JsonOptions);

            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            Assert.False(await db.BookmarkNodes.AnyAsync(item => item.Title == request.Title));
            Assert.False(await db.ExtensionCommands.AnyAsync(item =>
                item.PayloadJson != null && item.PayloadJson.Contains(request.Title)));
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS FailTrackedSeriesInsert;");
        }
    }

    [Fact]
    public async Task CheckRelease_UpdatesSeriesAndLogsEvent()
    {
        _factory.MockProvider.LatestRelease = new LibraryReleaseInfo("15", null, DateTimeOffset.UtcNow, "https://example.com/source");

        using var client = _factory.CreateClient();

        var request = new TrackLibraryEntryRequest
        {
            ParentId = Guid.Empty,
            Provider = "MockProvider",
            ProviderId = "test-check",
            Title = "Checked Series",
            MediaType = LibraryMediaType.Manga,
            LatestChapter = "10",
            SourceUrl = "https://example.com/source"
        };
        using var trackResponse = await client.PostAsJsonAsync("/api/library/track", request, JsonOptions);
        trackResponse.EnsureSuccessStatusCode();
        var bookmark = await trackResponse.Content.ReadFromJsonAsync<BookmarkNodeDto>(JsonOptions);
        Assert.NotNull(bookmark);

        using var checkResponse = await client.PostAsync($"/api/library/track/{bookmark!.Id}/check", null);
        checkResponse.EnsureSuccessStatusCode();
        var updated = await checkResponse.Content.ReadFromJsonAsync<TrackedSeriesDto>(JsonOptions);
        Assert.NotNull(updated);
        Assert.Equal("15", updated!.LatestKnownChapter);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var events = await db.ReleaseEvents.Where(e => e.TrackedSeriesId == updated.Id).ToListAsync();
        var ev = Assert.Single(events);
        Assert.Equal("15", ev.Chapter);
    }

    public void Dispose() => _factory.Dispose();

    private sealed class FakeMediaProvider : IMediaProvider
    {
        public string ProviderName => "MockProvider";
        public bool IsEnabled => true;
        public LibraryReleaseInfo? LatestRelease { get; set; }

        public Task<IReadOnlyList<LibraryEntryDto>> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<LibraryEntryDto>>([]);

        public Task<LibraryEntryDto?> GetDetailsAsync(string providerId, CancellationToken cancellationToken)
            => Task.FromResult<LibraryEntryDto?>(null);

        public Task<LibraryReleaseInfo?> GetLatestReleaseAsync(string providerId, CancellationToken cancellationToken)
            => Task.FromResult(LatestRelease);
    }

    private sealed class LibraryTrackingTestFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bm-library-track-{Guid.NewGuid():N}.db");
        public FakeMediaProvider MockProvider { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_dbPath}"));

                services.AddSingleton<IMediaProvider>(MockProvider);

                using var sp = services.BuildServiceProvider();
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureDeleted();
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            SqliteConnection.ClearAllPools();
            try
            {
                if (File.Exists(_dbPath))
                    File.Delete(_dbPath);
            }
            catch
            {
            }
        }
    }
}
