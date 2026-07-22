using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Embedding;
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
        Func<string, string?, CatalogPageResult>? onGetPage = null,
        Func<string, LibraryEntryDto?>? onGetDetails = null,
        bool listingProvidesFullSynopsis = false) : IBulkCatalogProvider
    {
        public string ProviderName => name;
        public bool IsEnabled => true;
        public IReadOnlyList<string> CatalogMediaTypeQueries { get; } = queries;
        public bool ListingProvidesFullSynopsis => listingProvidesFullSynopsis;
        public int GetDetailsCallCount { get; private set; }

        public Task<IReadOnlyList<LibraryEntryDto>> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LibraryEntryDto>>([]);

        public Task<LibraryEntryDto?> GetDetailsAsync(string providerId, CancellationToken cancellationToken)
        {
            GetDetailsCallCount++;
            return Task.FromResult(onGetDetails?.Invoke(providerId));
        }

        public Task<CatalogPageResult> GetCatalogPageAsync(string mediaTypeQuery, string? continuationToken, CancellationToken cancellationToken) =>
            Task.FromResult(onGetPage is not null
                ? onGetPage(mediaTypeQuery, continuationToken)
                : new CatalogPageResult([], null));
    }

    private static LibraryEntryDto MakeEntry(
        string providerId,
        string title = "Title",
        string provider = "TestProvider",
        string? synopsis = null,
        string? latestChapter = null,
        IReadOnlyList<string>? genres = null) => new(
        provider,
        providerId,
        title,
        [],
        [],
        LibraryMediaType.Manga,
        null,
        synopsis,
        genres ?? [],
        null,
        null,
        latestChapter,
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
        params IMediaProvider[] providers) =>
        CreateService(db, new FakeEmbeddingService(), providers);

    private static (LibraryCatalogSyncBackgroundService Service, IServiceScopeFactory ScopeFactory) CreateService(
        AppDbContext db,
        IEmbeddingService embeddingService,
        params IMediaProvider[] providers)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var registry = new LibraryProviderRegistry(providers, scopeFactory);
        var matchService = new BookmarkSeriesMatchService(scopeFactory, NullLogger<BookmarkSeriesMatchService>.Instance);
        var service = new LibraryCatalogSyncBackgroundService(scopeFactory, NullLogger<LibraryCatalogSyncBackgroundService>.Instance, registry, matchService, embeddingService);
        return (service, scopeFactory);
    }

    /// <summary>Deterministic stand-in for the ONNX embedder: emits a fixed-length vector seeded off the
    /// text length so tests never touch a real model. Configurable readiness and a throw mode let tests
    /// assert graceful degradation.</summary>
    private sealed class FakeEmbeddingService(bool isReady = true, bool throwOnEmbed = false) : IEmbeddingService
    {
        public int EmbedCallCount { get; private set; }
        public bool IsReady { get; } = isReady;

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            EmbedCallCount++;
            if (throwOnEmbed)
                throw new InvalidOperationException("embedding backend unavailable");
            return Task.FromResult(MakeVector(text));
        }

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            EmbedCallCount++;
            if (throwOnEmbed)
                throw new InvalidOperationException("embedding backend unavailable");
            return Task.FromResult<IReadOnlyList<float[]>>(texts.Select(MakeVector).ToList());
        }

        private static float[] MakeVector(string text)
        {
            var vector = new float[EmbeddingConstants.EmbeddingDimensions];
            vector[0] = text.Length;
            return vector;
        }
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

    [Fact]
    public void ApplyDto_PreservesEnrichedFieldsWhenIncomingListingStubIsThin()
    {
        var row = new LibraryCatalogEntry
        {
            Synopsis = "Existing synopsis",
            LatestChapter = "100",
            Genres = "Fantasy,Action",
            Authors = "Some Author",
            Rating = 4.5
        };

        var thinListing = MakeEntry("shadow-slave", synopsis: null, latestChapter: null);
        LibraryCatalogSyncBackgroundService.ApplyDto(row, thinListing);

        Assert.Equal("Existing synopsis", row.Synopsis);
        Assert.Equal("100", row.LatestChapter);
        Assert.Equal("Fantasy,Action", row.Genres);
        Assert.Equal("Some Author", row.Authors);
        Assert.Equal(4.5, row.Rating);
    }

    [Fact]
    public async Task ProcessQueueItemAsync_NovelfireEnrichesThinEntriesFromDetailPage()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var provider = new FakeBulkProvider(
            "Novelfire",
            ["genre-all"],
            (_, _) => new CatalogPageResult([MakeEntry("shadow-slave", provider: "Novelfire", synopsis: null, latestChapter: null)], null),
            id => new LibraryEntryDto(
                "Novelfire",
                id,
                "Shadow Slave",
                [],
                ["Guiltythree"],
                LibraryMediaType.Webnovel,
                null,
                "Growing up in poverty, Sunny never expected anything good from life.",
                ["Fantasy", "Action"],
                null,
                "Ongoing",
                "3090",
                null,
                null,
                "https://novelfire.net/book/shadow-slave"));
        var (service, _) = CreateService(db, provider);

        var item = new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "Novelfire",
            MediaTypeQuery = "genre-all",
            Status = CatalogSyncQueueStatus.Processing,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.LibraryCatalogSyncQueue.Add(item);
        await db.SaveChangesAsync();

        await service.ProcessQueueItemAsync(provider, item, CancellationToken.None);

        var row = await db.LibraryCatalogEntries.SingleAsync();
        Assert.Equal("Growing up in poverty, Sunny never expected anything good from life.", row.Synopsis);
        Assert.Equal("3090", row.LatestChapter);
        Assert.Equal("Fantasy,Action", row.Genres);
        Assert.Equal("Ongoing", row.Status);
    }

    [Fact]
    public async Task ProcessQueueItemAsync_ThinProviderNotOnLegacyAllowlist_StillEnrichesFromDetailPage()
    {
        // Widened behavior: enrichment is driven by ListingProvidesFullSynopsis (default false =>
        // thin => enrich), not a hardcoded Novelfire/RanobeDB name allowlist. Any bulk provider whose
        // listing rows are thin gets detail-enriched so its catalog rows carry a synopsis for RAG.
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var provider = new FakeBulkProvider(
            "SomeNewProvider",
            ["seq1"],
            (_, _) => new CatalogPageResult([MakeEntry("n1", provider: "SomeNewProvider", synopsis: null, latestChapter: null)], null),
            id => new LibraryEntryDto(
                "SomeNewProvider",
                id,
                "New Series",
                [],
                ["Author"],
                LibraryMediaType.Webnovel,
                null,
                "A rich synopsis fetched from the detail page.",
                ["Fantasy"],
                null,
                "Ongoing",
                "42",
                null,
                null,
                "https://example.com/n1"));
        var (service, _) = CreateService(db, provider);

        var item = new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "SomeNewProvider",
            MediaTypeQuery = "seq1",
            Status = CatalogSyncQueueStatus.Processing,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.LibraryCatalogSyncQueue.Add(item);
        await db.SaveChangesAsync();

        await service.ProcessQueueItemAsync(provider, item, CancellationToken.None);

        var row = await db.LibraryCatalogEntries.SingleAsync();
        Assert.Equal("A rich synopsis fetched from the detail page.", row.Synopsis);
        Assert.Equal("Fantasy", row.Genres);
        Assert.Equal(1, provider.GetDetailsCallCount);
    }

    [Fact]
    public async Task ProcessQueueItemAsync_ProviderWithFullListingSynopsis_DoesNotFetchDetails()
    {
        // Providers whose listing already returns the synopsis (AniList, MangaDex) opt out via
        // ListingProvidesFullSynopsis => a per-title detail fetch would only re-return listing data,
        // so it must be skipped entirely to avoid wasted calls.
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var provider = new FakeBulkProvider(
            "RichListingProvider",
            ["seq1"],
            (_, _) => new CatalogPageResult(
                [MakeEntry("r1", provider: "RichListingProvider", synopsis: "Already has a synopsis at listing time.")],
                null),
            onGetDetails: _ => throw new InvalidOperationException("details must not be fetched"),
            listingProvidesFullSynopsis: true);
        var (service, _) = CreateService(db, provider);

        var item = new LibraryCatalogSyncQueueItem
        {
            Id = Guid.NewGuid(),
            Provider = "RichListingProvider",
            MediaTypeQuery = "seq1",
            Status = CatalogSyncQueueStatus.Processing,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.LibraryCatalogSyncQueue.Add(item);
        await db.SaveChangesAsync();

        await service.ProcessQueueItemAsync(provider, item, CancellationToken.None);

        var row = await db.LibraryCatalogEntries.SingleAsync();
        Assert.Equal("Already has a synopsis at listing time.", row.Synopsis);
        Assert.Equal(0, provider.GetDetailsCallCount);
    }

    [Fact]
    public async Task ProcessQueueItemAsync_EmbeddingReady_SetsEmbeddingBlobAndSourceHash()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var provider = new FakeBulkProvider(
            "TestProvider",
            ["seq1"],
            (_, _) => new CatalogPageResult([MakeEntry("p1", "Embed Me")], null));
        var (service, _) = CreateService(db, new FakeEmbeddingService(), provider);

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

        var row = await db.LibraryCatalogEntries.SingleAsync();
        Assert.NotNull(row.Embedding);
        Assert.NotNull(row.EmbeddingSourceHash);
        Assert.Equal(LibraryEmbeddingText.Hash(LibraryEmbeddingText.Build(row)), row.EmbeddingSourceHash);
        Assert.Equal(EmbeddingConstants.EmbeddingDimensions, row.GetEmbeddingVector()!.Length);
    }

    [Fact]
    public async Task ProcessQueueItemAsync_EmbeddingNotReady_SavesEntriesWithoutEmbedding()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var provider = new FakeBulkProvider(
            "TestProvider",
            ["seq1"],
            (_, _) => new CatalogPageResult([MakeEntry("p1")], null));
        var embedding = new FakeEmbeddingService(isReady: false);
        var (service, _) = CreateService(db, embedding, provider);

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

        var row = await db.LibraryCatalogEntries.SingleAsync();
        Assert.Null(row.Embedding);
        Assert.Equal(0, embedding.EmbedCallCount);
        Assert.Equal(CatalogSyncQueueStatus.Done, (await db.LibraryCatalogSyncQueue.FirstAsync(q => q.Id == item.Id)).Status);
    }

    [Fact]
    public async Task ProcessQueueItemAsync_EmbeddingThrows_DoesNotFailCrawl()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var provider = new FakeBulkProvider(
            "TestProvider",
            ["seq1"],
            (_, _) => new CatalogPageResult([MakeEntry("p1")], null));
        var (service, _) = CreateService(db, new FakeEmbeddingService(throwOnEmbed: true), provider);

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

        // Embedding blows up, but the entry is already persisted and the queue item still completes.
        await service.ProcessQueueItemAsync(provider, item, CancellationToken.None);

        var row = await db.LibraryCatalogEntries.SingleAsync();
        Assert.Null(row.Embedding);
        Assert.Equal(CatalogSyncQueueStatus.Done, (await db.LibraryCatalogSyncQueue.FirstAsync(q => q.Id == item.Id)).Status);
    }

    [Fact]
    public async Task ProcessQueueItemAsync_UnchangedEmbedText_DoesNotReEmbed()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var provider = new FakeBulkProvider(
            "TestProvider",
            ["seq1"],
            (_, _) => new CatalogPageResult([MakeEntry("p1", "Stable Title")], null));
        var embedding = new FakeEmbeddingService();
        var (service, _) = CreateService(db, embedding, provider);

        LibraryCatalogSyncQueueItem MakeItem() => new()
        {
            Id = Guid.NewGuid(),
            Provider = "TestProvider",
            MediaTypeQuery = "seq1",
            Status = CatalogSyncQueueStatus.Processing,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var first = MakeItem();
        db.LibraryCatalogSyncQueue.Add(first);
        await db.SaveChangesAsync();
        await service.ProcessQueueItemAsync(provider, first, CancellationToken.None);
        var afterFirst = embedding.EmbedCallCount;

        var second = MakeItem();
        db.LibraryCatalogSyncQueue.Add(second);
        await db.SaveChangesAsync();
        await service.ProcessQueueItemAsync(provider, second, CancellationToken.None);

        Assert.Equal(1, afterFirst);
        // Second pass upserts the same text, so the hash matches and no new embed call is made.
        Assert.Equal(1, embedding.EmbedCallCount);
    }
}

