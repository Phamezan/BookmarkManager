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

public sealed class LibraryCatalogSyncBackgroundServiceTests
{
    private sealed class FakeBulkProvider(
        string name,
        IReadOnlyList<string> queries,
        Func<string, string?, CatalogPageResult>? onGetPage = null) : IBulkCatalogProvider
    {
        public string ProviderName => name;
        public bool IsEnabled => true;
        public IReadOnlyList<string> CatalogMediaTypeQueries { get; } = queries;

        public Task<IReadOnlyList<LibraryEntryDto>> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LibraryEntryDto>>([]);

        public Task<LibraryEntryDto?> GetDetailsAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult<LibraryEntryDto?>(null);

        public Task<LibraryReleaseInfo?> GetLatestReleaseAsync(string providerId, CancellationToken cancellationToken) =>
            Task.FromResult<LibraryReleaseInfo?>(null);

        public Task<CatalogPageResult> GetCatalogPageAsync(string mediaTypeQuery, string? continuationToken, CancellationToken cancellationToken) =>
            Task.FromResult(onGetPage is not null
                ? onGetPage(mediaTypeQuery, continuationToken)
                : new CatalogPageResult([], null));
    }

    private static LibraryEntryDto MakeEntry(string providerId, string title = "Title") => new(
        "TestProvider",
        providerId,
        title,
        [],
        [],
        LibraryMediaType.Manga,
        null,
        null,
        [],
        null,
        null,
        null,
        null,
        null,
        $"https://example.com/{providerId}");

    private sealed class TestDatabase : IDisposable
    {
        private readonly SqliteConnection _connection;
        public AppDbContext Db { get; }

        public TestDatabase()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
            Db = new AppDbContext(options);
            Db.Database.EnsureCreated();
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    private static (LibraryCatalogSyncBackgroundService Service, IServiceScopeFactory ScopeFactory) CreateService(
        AppDbContext db,
        params IMediaProvider[] providers)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var registry = new LibraryProviderRegistry(providers, scopeFactory);
        var service = new LibraryCatalogSyncBackgroundService(scopeFactory, NullLogger<LibraryCatalogSyncBackgroundService>.Instance, registry);
        return (service, scopeFactory);
    }

    [Fact]
    public async Task EnsureQueueSeededAsync_FreshInstall_SeedsUnboundedItemPerSequence()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var provider = new FakeBulkProvider("TestProvider", ["seq1", "seq2"]);
        var (service, _) = CreateService(db, provider);

        await service.EnsureQueueSeededAsync(forceFullUnboundedCrawl: false, CancellationToken.None);

        var items = await db.LibraryCatalogSyncQueue.ToListAsync();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Null(i.RemainingPages));
        Assert.All(items, i => Assert.Equal(CatalogSyncQueueStatus.Pending, i.Status));
    }

    [Fact]
    public async Task EnsureQueueSeededAsync_CatalogAlreadyPopulated_SeedsBoundedTopUp()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        db.LibraryCatalogEntries.Add(new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            ProviderId = "existing-1",
            Title = "Existing",
            SourceUrl = "https://example.com/existing-1",
            FirstImportedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var provider = new FakeBulkProvider("TestProvider", ["seq1"]);
        var (service, _) = CreateService(db, provider);

        await service.EnsureQueueSeededAsync(forceFullUnboundedCrawl: false, CancellationToken.None);

        var item = await db.LibraryCatalogSyncQueue.SingleAsync();
        Assert.Equal(2, item.RemainingPages);
    }

    [Fact]
    public async Task EnsureQueueSeededAsync_ForceFullResync_IgnoresExistingCatalogAndSeedsUnbounded()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        db.LibraryCatalogEntries.Add(new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            ProviderId = "existing-1",
            Title = "Existing",
            SourceUrl = "https://example.com/existing-1",
            FirstImportedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var provider = new FakeBulkProvider("TestProvider", ["seq1"]);
        var (service, _) = CreateService(db, provider);

        await service.EnsureQueueSeededAsync(forceFullUnboundedCrawl: true, CancellationToken.None);

        var item = await db.LibraryCatalogSyncQueue.SingleAsync();
        Assert.Null(item.RemainingPages);
    }

    [Fact]
    public async Task EnsureQueueSeededAsync_ActiveItemAlreadyQueued_DoesNotDuplicate()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        db.LibraryCatalogSyncQueue.Add(new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            MediaTypeQuery = "seq1",
            Status = CatalogSyncQueueStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var provider = new FakeBulkProvider("TestProvider", ["seq1"]);
        var (service, _) = CreateService(db, provider);

        await service.EnsureQueueSeededAsync(forceFullUnboundedCrawl: false, CancellationToken.None);

        Assert.Equal(1, await db.LibraryCatalogSyncQueue.CountAsync());
    }

    [Fact]
    public async Task EnsureQueueSeededAsync_DisabledProvider_SkipsSeeding()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        db.AppConfig.Add(new AppConfig
        {
            Id = AppConfigConstants.SingletonId,
            DisabledProviders = "TestProvider"
        });
        await db.SaveChangesAsync();

        var provider = new FakeBulkProvider("TestProvider", ["seq1"]);
        var (service, _) = CreateService(db, provider);

        await service.EnsureQueueSeededAsync(forceFullUnboundedCrawl: false, CancellationToken.None);

        Assert.Equal(0, await db.LibraryCatalogSyncQueue.CountAsync());
    }

    [Fact]
    public async Task ProcessQueueItemAsync_WithNextToken_UpsertsEntriesAndChainsNextItem()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var provider = new FakeBulkProvider(
            "TestProvider",
            ["seq1"],
            (_, token) => new CatalogPageResult([MakeEntry("p1"), MakeEntry("p2")], "next-token", RankBase: 0));
        var (service, _) = CreateService(db, provider);

        var item = new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            MediaTypeQuery = "seq1",
            Status = CatalogSyncQueueStatus.Processing,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.LibraryCatalogSyncQueue.Add(item);
        await db.SaveChangesAsync();

        await service.ProcessQueueItemAsync(provider, item, CancellationToken.None);

        Assert.Equal(2, await db.LibraryCatalogEntries.CountAsync());
        var current = await db.LibraryCatalogSyncQueue.FirstAsync(q => q.Id == item.Id);
        Assert.Equal(CatalogSyncQueueStatus.Done, current.Status);

        var chained = await db.LibraryCatalogSyncQueue.SingleAsync(q => q.Id != item.Id);
        Assert.Equal("next-token", chained.ContinuationToken);
        Assert.Equal(CatalogSyncQueueStatus.Pending, chained.Status);
    }

    [Fact]
    public async Task ProcessQueueItemAsync_ProviderReturnsSameTokenAsCurrent_StopsChainInsteadOfLooping()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var provider = new FakeBulkProvider(
            "TestProvider",
            ["seq1"],
            (_, token) => new CatalogPageResult([], token));
        var (service, _) = CreateService(db, provider);

        var item = new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            MediaTypeQuery = "seq1",
            ContinuationToken = "stuck-token",
            Status = CatalogSyncQueueStatus.Processing,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.LibraryCatalogSyncQueue.Add(item);
        await db.SaveChangesAsync();

        await service.ProcessQueueItemAsync(provider, item, CancellationToken.None);

        Assert.Equal(1, await db.LibraryCatalogSyncQueue.CountAsync());
        var current = await db.LibraryCatalogSyncQueue.SingleAsync();
        Assert.Equal(CatalogSyncQueueStatus.Done, current.Status);
    }

    [Fact]
    public async Task ProcessQueueItemAsync_NoNextToken_MarksDoneWithoutChaining()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var provider = new FakeBulkProvider(
            "TestProvider",
            ["seq1"],
            (_, _) => new CatalogPageResult([MakeEntry("p1")], null));
        var (service, _) = CreateService(db, provider);

        var item = new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            MediaTypeQuery = "seq1",
            Status = CatalogSyncQueueStatus.Processing,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.LibraryCatalogSyncQueue.Add(item);
        await db.SaveChangesAsync();

        await service.ProcessQueueItemAsync(provider, item, CancellationToken.None);

        Assert.Equal(1, await db.LibraryCatalogSyncQueue.CountAsync());
        Assert.Equal(CatalogSyncQueueStatus.Done, (await db.LibraryCatalogSyncQueue.SingleAsync()).Status);
    }

    [Fact]
    public async Task ProcessQueueItemAsync_BudgetExhausted_StopsChainEvenWithNextToken()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var provider = new FakeBulkProvider(
            "TestProvider",
            ["seq1"],
            (_, _) => new CatalogPageResult([MakeEntry("p1")], "next-token"));
        var (service, _) = CreateService(db, provider);

        var item = new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            MediaTypeQuery = "seq1",
            RemainingPages = 0,
            Status = CatalogSyncQueueStatus.Processing,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.LibraryCatalogSyncQueue.Add(item);
        await db.SaveChangesAsync();

        await service.ProcessQueueItemAsync(provider, item, CancellationToken.None);

        Assert.Equal(1, await db.LibraryCatalogSyncQueue.CountAsync());
    }

    [Fact]
    public async Task ProcessQueueItemAsync_UpsertMatchingProviderId_UpdatesExistingRowInsteadOfDuplicating()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        db.LibraryCatalogEntries.Add(new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            ProviderId = "p1",
            Title = "Old Title",
            SourceUrl = "https://example.com/p1",
            FirstImportedAt = DateTimeOffset.UtcNow.AddDays(-1),
            LastRefreshedAt = DateTimeOffset.UtcNow.AddDays(-1)
        });
        await db.SaveChangesAsync();

        var provider = new FakeBulkProvider(
            "TestProvider",
            ["seq1"],
            (_, _) => new CatalogPageResult([MakeEntry("p1", "New Title")], null));
        var (service, _) = CreateService(db, provider);

        var item = new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            MediaTypeQuery = "seq1",
            Status = CatalogSyncQueueStatus.Processing,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.LibraryCatalogSyncQueue.Add(item);
        await db.SaveChangesAsync();

        await service.ProcessQueueItemAsync(provider, item, CancellationToken.None);

        Assert.Equal(1, await db.LibraryCatalogEntries.CountAsync());
        Assert.Equal("New Title", (await db.LibraryCatalogEntries.SingleAsync()).Title);
    }

    [Fact]
    public async Task ProcessQueueItemAsync_ProviderThrows_RequeuesWithBackoff()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var provider = new FakeBulkProvider(
            "TestProvider",
            ["seq1"],
            (_, _) => throw new InvalidOperationException("provider unavailable"));
        var (service, _) = CreateService(db, provider);

        var item = new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            MediaTypeQuery = "seq1",
            Status = CatalogSyncQueueStatus.Processing,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.LibraryCatalogSyncQueue.Add(item);
        await db.SaveChangesAsync();

        // ProcessQueueItemAsync catches the provider exception internally and requeues with backoff
        // rather than propagating it - a single flaky page must not take down the worker loop.
        await service.ProcessQueueItemAsync(provider, item, CancellationToken.None);

        var row = await db.LibraryCatalogSyncQueue.SingleAsync();
        Assert.Equal(1, row.Attempts);
        Assert.Equal(CatalogSyncQueueStatus.Pending, row.Status);
        Assert.NotNull(row.NextAttemptAt);
        Assert.True(row.NextAttemptAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task ClaimNextPendingItemAsync_AgainstRealSqlite_TranslatesQueryAndClaimsOldestDueItem()
    {
        // Regression test: the claim query mixes an equality predicate with a NextAttemptAt <= now
        // comparison. EF Core's SQLite provider can't translate relational operators on DateTimeOffset
        // columns, and a prior version of this method threw InvalidOperationException on every call -
        // silently killing the (unobserved) background polling loop with zero titles ever synced. This
        // test runs against a real SQLite provider (not InMemory) so it actually exercises translation.
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var older = new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            MediaTypeQuery = "seq1",
            Status = CatalogSyncQueueStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        var newer = new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            MediaTypeQuery = "seq2",
            Status = CatalogSyncQueueStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        var notYetDue = new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            MediaTypeQuery = "seq3",
            Status = CatalogSyncQueueStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-20),
            NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1)
        };
        var otherProvider = new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "OtherProvider",
            MediaTypeQuery = "seq1",
            Status = CatalogSyncQueueStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30)
        };
        db.LibraryCatalogSyncQueue.AddRange(older, newer, notYetDue, otherProvider);
        await db.SaveChangesAsync();

        var provider = new FakeBulkProvider("TestProvider", ["seq1", "seq2", "seq3"]);
        var (service, _) = CreateService(db, provider);

        var claimed = await service.ClaimNextPendingItemAsync("TestProvider", CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.Equal(older.Id, claimed!.Id);
        var persisted = await db.LibraryCatalogSyncQueue.FirstAsync(q => q.Id == older.Id);
        Assert.Equal(CatalogSyncQueueStatus.Processing, persisted.Status);
    }

    [Fact]
    public async Task ClaimNextPendingItemAsync_NoDueItems_ReturnsNull()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        db.LibraryCatalogSyncQueue.Add(new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            MediaTypeQuery = "seq1",
            Status = CatalogSyncQueueStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow.AddHours(1)
        });
        await db.SaveChangesAsync();

        var provider = new FakeBulkProvider("TestProvider", ["seq1"]);
        var (service, _) = CreateService(db, provider);

        var claimed = await service.ClaimNextPendingItemAsync("TestProvider", CancellationToken.None);

        Assert.Null(claimed);
    }

    [Fact]
    public async Task RequeueWithBackoffAsync_MaxAttemptsReached_MarksFailed()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var provider = new FakeBulkProvider("TestProvider", ["seq1"]);
        var (service, _) = CreateService(db, provider);

        var item = new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            MediaTypeQuery = "seq1",
            Attempts = 4,
            Status = CatalogSyncQueueStatus.Processing,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.LibraryCatalogSyncQueue.Add(item);
        await db.SaveChangesAsync();

        await service.RequeueWithBackoffAsync(item.Id, "still failing", CancellationToken.None);

        var row = await db.LibraryCatalogSyncQueue.SingleAsync();
        Assert.Equal(5, row.Attempts);
        Assert.Equal(CatalogSyncQueueStatus.Failed, row.Status);
    }

    [Fact]
    public async Task GetStatusAsync_ReportsQueueDepthAndCatalogSize()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        db.LibraryCatalogEntries.Add(new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            ProviderId = "p1",
            Title = "Title",
            SourceUrl = "https://example.com/p1",
            FirstImportedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow
        });
        db.LibraryCatalogSyncQueue.Add(new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            MediaTypeQuery = "seq1",
            Status = CatalogSyncQueueStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.LibraryCatalogSyncQueue.Add(new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            MediaTypeQuery = "seq2",
            Status = CatalogSyncQueueStatus.Failed,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var provider = new FakeBulkProvider("TestProvider", ["seq1", "seq2"]);
        var (service, _) = CreateService(db, provider);

        var status = await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal(1, status.TotalEntries);
        Assert.Equal(1, status.PendingQueueCount);
        Assert.Equal(1, status.FailedQueueCount);
        Assert.True(status.IsCrawling);
    }
}

