using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Embedding;
using BookmarkManager.Api.Services.Library;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

public sealed class LibraryEmbeddingBackfillServiceTests
{
    private sealed class TestDatabase : IDisposable
    {
        private readonly SqliteConnection _connection;
        public AppDbContext Db { get; }
        public IServiceScopeFactory ScopeFactory { get; }

        public TestDatabase()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
            Db = new AppDbContext(options);
            Db.Database.EnsureCreated();

            var services = new ServiceCollection();
            services.AddSingleton(Db);
            ScopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        }

        public void Dispose()
        {
            Db.Dispose();
            _connection.Dispose();
        }
    }

    private sealed class FakeEmbeddingService(bool isReady = true, bool throwOnEmbed = false) : IEmbeddingService
    {
        public int EmbeddedTextCount { get; private set; }
        public bool IsReady { get; } = isReady;

        public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken) => EmbedAsync(text, cancellationToken);


        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            EmbeddedTextCount++;
            if (throwOnEmbed)
                throw new InvalidOperationException("embedding backend unavailable");
            return Task.FromResult(MakeVector(text));
        }

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
        {
            EmbeddedTextCount += texts.Count;
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

    private sealed class FakeVectorSearchService : IVectorSearchService
    {
        public int InvalidateCount { get; private set; }
        public void InvalidateCatalog() => InvalidateCount++;
        public Task<IReadOnlyList<(Guid Id, float Score)>> SearchAsync(float[] query, int k, float floor, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<(Guid, float)>>([]);
    }

    private static LibraryCatalogEntry MakeRow(string title, byte[]? embedding = null, string? hash = null) => new()
    {
        Id = Guid.NewGuid(),
        Provider = "TestProvider",
        ProviderId = Guid.NewGuid().ToString(),
        Title = title,
        SourceUrl = $"https://example.com/{title}",
        FirstImportedAt = DateTimeOffset.UtcNow,
        LastRefreshedAt = DateTimeOffset.UtcNow,
        Embedding = embedding,
        EmbeddingSourceHash = hash
    };

    private static LibraryEmbeddingBackfillService CreateService(
        IServiceScopeFactory scopeFactory,
        IEmbeddingService embeddingService,
        IVectorSearchService? vectorSearch = null) =>
        new(scopeFactory, embeddingService, vectorSearch ?? new FakeVectorSearchService(), NullLogger<LibraryEmbeddingBackfillService>.Instance);

    [Fact]
    public async Task RunBackfillPassAsync_EmbedsRowsWithNullEmbedding()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        db.LibraryCatalogEntries.AddRange(MakeRow("Alpha"), MakeRow("Beta"));
        await db.SaveChangesAsync();

        var embedding = new FakeEmbeddingService();
        var vectorSearch = new FakeVectorSearchService();
        var service = CreateService(testDb.ScopeFactory, embedding, vectorSearch);

        var count = await service.RunBackfillPassAsync(CancellationToken.None);

        Assert.Equal(2, count);
        Assert.Equal(2, embedding.EmbeddedTextCount);
        var rows = await db.LibraryCatalogEntries.ToListAsync();
        Assert.All(rows, r =>
        {
            Assert.NotNull(r.Embedding);
            Assert.Equal(LibraryEmbeddingText.SourceHash(r), r.EmbeddingSourceHash);
        });

        // Writing embeddings must invalidate the in-memory vector cache - its count-only self-heal can't
        // detect a re-embed that leaves the total embedded-row count unchanged (see VectorSearchService).
        Assert.Equal(1, vectorSearch.InvalidateCount);
    }

    [Fact]
    public async Task RunBackfillPassAsync_ReEmbedsRowWithStaleHash()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var stale = MakeRow("Gamma", embedding: [1, 2, 3, 4], hash: "stale-hash-does-not-match");
        db.LibraryCatalogEntries.Add(stale);
        await db.SaveChangesAsync();

        var embedding = new FakeEmbeddingService();
        var service = CreateService(testDb.ScopeFactory, embedding);

        var count = await service.RunBackfillPassAsync(CancellationToken.None);

        Assert.Equal(1, count);
        var row = await db.LibraryCatalogEntries.SingleAsync();
        Assert.Equal(LibraryEmbeddingText.SourceHash(row), row.EmbeddingSourceHash);
        Assert.Equal(EmbeddingConstants.EmbeddingDimensions, row.GetEmbeddingVector()!.Length);
    }

    [Fact]
    public async Task RunBackfillPassAsync_SkipsRowsAlreadyCurrent()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var row = MakeRow("Delta");
        var currentHash = LibraryEmbeddingText.SourceHash(row);
        row.SetEmbeddingVector(new float[EmbeddingConstants.EmbeddingDimensions]);
        row.EmbeddingSourceHash = currentHash;
        db.LibraryCatalogEntries.Add(row);
        await db.SaveChangesAsync();

        var embedding = new FakeEmbeddingService();
        var vectorSearch = new FakeVectorSearchService();
        var service = CreateService(testDb.ScopeFactory, embedding, vectorSearch);

        var count = await service.RunBackfillPassAsync(CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Equal(0, embedding.EmbeddedTextCount);
        Assert.Equal(0, vectorSearch.InvalidateCount);
    }

    [Fact]
    public async Task RunBackfillPassAsync_EmptyCatalog_EmbedsNothing()
    {
        using var testDb = new TestDatabase();
        var embedding = new FakeEmbeddingService();
        var vectorSearch = new FakeVectorSearchService();
        var service = CreateService(testDb.ScopeFactory, embedding, vectorSearch);

        var count = await service.RunBackfillPassAsync(CancellationToken.None);

        Assert.Equal(0, count);
        Assert.Equal(0, embedding.EmbeddedTextCount);
        Assert.Equal(0, vectorSearch.InvalidateCount);
    }

    [Fact]
    public async Task RunBackfillPassAsync_MoreRowsThanBatchSize_EmbedsAll()
    {
        using var testDb = new TestDatabase();
        var db = testDb.Db;
        var rowCount = EmbeddingConstants.BackfillBatchSize + 5;
        for (var i = 0; i < rowCount; i++)
            db.LibraryCatalogEntries.Add(MakeRow($"Title-{i}"));
        await db.SaveChangesAsync();

        var embedding = new FakeEmbeddingService();
        var service = CreateService(testDb.ScopeFactory, embedding);

        var count = await service.RunBackfillPassAsync(CancellationToken.None);

        Assert.Equal(rowCount, count);
        Assert.Equal(rowCount, await db.LibraryCatalogEntries.CountAsync(e => e.Embedding != null));
    }
}
