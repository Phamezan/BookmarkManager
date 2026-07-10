using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

public sealed class ReleaseWatcherBackgroundServiceTests
{
    private sealed class FakeProvider(string name, bool isEnabled, LibraryReleaseInfo? info) : IMediaProvider
    {
        public string ProviderName => name;
        public bool IsEnabled => isEnabled;

        public Task<IReadOnlyList<LibraryEntryDto>> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<LibraryEntryDto>>([]);

        public Task<LibraryEntryDto?> GetDetailsAsync(string providerId, CancellationToken cancellationToken)
            => Task.FromResult<LibraryEntryDto?>(null);

        public Task<LibraryReleaseInfo?> GetLatestReleaseAsync(string providerId, CancellationToken cancellationToken)
            => Task.FromResult(info);
    }

    private sealed class ThrowingProvider(string name) : IMediaProvider
    {
        public string ProviderName => name;
        public bool IsEnabled => true;

        public Task<IReadOnlyList<LibraryEntryDto>> SearchAsync(
            string query,
            LibraryMediaType? mediaType,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LibraryEntryDto>>([]);

        public Task<LibraryEntryDto?> GetDetailsAsync(
            string providerId,
            CancellationToken cancellationToken) =>
            Task.FromResult<LibraryEntryDto?>(null);

        public Task<LibraryReleaseInfo?> GetLatestReleaseAsync(
            string providerId,
            CancellationToken cancellationToken) =>
            throw new HttpRequestException("Provider unavailable.");
    }

    [Fact]
    public async Task CheckAndUpdateSeriesAsync_NewerChapter_UpdatesSeriesAndCreatesEvent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var bookmark = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "My Novel",
            Type = NodeType.Bookmark,
            Url = "https://example.com/fiction"
        };
        db.BookmarkNodes.Add(bookmark);

        var series = new TrackedSeries
        {
            Id = Guid.NewGuid(),
            BookmarkId = bookmark.Id,
            Provider = "RoyalRoad",
            ProviderId = "novel-1",
            LatestKnownChapter = "10",
            ChaptersRead = 5,
            Status = "Reading"
        };
        db.TrackedSeries.Add(series);
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var fakeProvider = new FakeProvider("RoyalRoad", true, new LibraryReleaseInfo("12", null, DateTimeOffset.UtcNow, "https://example.com/ch12"));
        var registry = new LibraryProviderRegistry([fakeProvider], scopeFactory);

        var watcher = new ReleaseWatcherBackgroundService(scopeFactory, NullLogger<ReleaseWatcherBackgroundService>.Instance, registry);

        var changed = await watcher.CheckAndUpdateSeriesAsync(db, series, CancellationToken.None);

        Assert.True(changed);
        Assert.Equal("12", series.LatestKnownChapter);

        var loggedEvent = await db.ReleaseEvents.FirstOrDefaultAsync(e => e.TrackedSeriesId == series.Id);
        Assert.NotNull(loggedEvent);
        Assert.Equal("12", loggedEvent!.Chapter);
    }

    [Fact]
    public async Task CheckAndUpdateSeriesAsync_SameChapter_DoesNotUpdateOrLogEvent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var bookmark = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "My Novel",
            Type = NodeType.Bookmark,
            Url = "https://example.com/fiction"
        };
        db.BookmarkNodes.Add(bookmark);

        var series = new TrackedSeries
        {
            Id = Guid.NewGuid(),
            BookmarkId = bookmark.Id,
            Provider = "RoyalRoad",
            ProviderId = "novel-1",
            LatestKnownChapter = "10",
            ChaptersRead = 5,
            Status = "Reading"
        };
        db.TrackedSeries.Add(series);
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var fakeProvider = new FakeProvider("RoyalRoad", true, new LibraryReleaseInfo("10", null, DateTimeOffset.UtcNow, "https://example.com/ch10"));
        var registry = new LibraryProviderRegistry([fakeProvider], scopeFactory);

        var watcher = new ReleaseWatcherBackgroundService(scopeFactory, NullLogger<ReleaseWatcherBackgroundService>.Instance, registry);

        var changed = await watcher.CheckAndUpdateSeriesAsync(db, series, CancellationToken.None);

        Assert.False(changed);
        Assert.Equal("10", series.LatestKnownChapter);

        var loggedEvent = await db.ReleaseEvents.FirstOrDefaultAsync(e => e.TrackedSeriesId == series.Id);
        Assert.Null(loggedEvent);
    }

    [Fact]
    public async Task CheckAndUpdateSeriesAsync_ReplayedAfterServiceRestart_CreatesOneEvent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var bookmark = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "Restarted Series",
            Type = NodeType.Bookmark,
            Url = "https://example.com/series"
        };
        var series = new TrackedSeries
        {
            Id = Guid.NewGuid(),
            BookmarkId = bookmark.Id,
            Bookmark = bookmark,
            Provider = "MangaDex",
            ProviderId = "restart-1",
            LatestKnownChapter = "10",
            Status = "Reading"
        };
        db.AddRange(bookmark, series);
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        using var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var provider = new FakeProvider(
            "MangaDex",
            true,
            new LibraryReleaseInfo("11", null, DateTimeOffset.UtcNow, "https://example.com/ch11"));
        var registry = new LibraryProviderRegistry([provider], scopeFactory);

        var firstWatcher = new ReleaseWatcherBackgroundService(
            scopeFactory,
            NullLogger<ReleaseWatcherBackgroundService>.Instance,
            registry);
        Assert.True(await firstWatcher.CheckAndUpdateSeriesAsync(db, series, CancellationToken.None));

        var restartedWatcher = new ReleaseWatcherBackgroundService(
            scopeFactory,
            NullLogger<ReleaseWatcherBackgroundService>.Instance,
            registry);
        Assert.False(await restartedWatcher.CheckAndUpdateSeriesAsync(db, series, CancellationToken.None));

        Assert.Equal(1, await db.ReleaseEvents.CountAsync(item => item.TrackedSeriesId == series.Id));
    }

    [Fact]
    public async Task CheckAndUpdateSeriesAsync_ProviderFailure_PersistsBackoff()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var bookmark = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Title = "Failing Series",
            Type = NodeType.Bookmark,
            Url = "https://example.com/failing"
        };
        var series = new TrackedSeries
        {
            Id = Guid.NewGuid(),
            BookmarkId = bookmark.Id,
            Bookmark = bookmark,
            Provider = "FailingProvider",
            ProviderId = "failure-1",
            Status = "Reading"
        };
        db.AddRange(bookmark, series);
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton(db);
        using var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var registry = new LibraryProviderRegistry([new ThrowingProvider("FailingProvider")], scopeFactory);
        var watcher = new ReleaseWatcherBackgroundService(
            scopeFactory,
            NullLogger<ReleaseWatcherBackgroundService>.Instance,
            registry);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            watcher.CheckAndUpdateSeriesAsync(db, series, CancellationToken.None));

        Assert.Equal(1, series.ConsecutiveFailureCount);
        Assert.NotNull(series.NextCheckAt);
        Assert.True(series.NextCheckAt > DateTimeOffset.UtcNow);
        Assert.Equal("Provider unavailable.", series.LastCheckError);
    }
}
