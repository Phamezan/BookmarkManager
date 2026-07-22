using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Embedding;
using BookmarkManager.Api.Services.Search;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BookmarkManager.UnitTests.Search;

public sealed class HybridSearchServiceTests
{
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

    private sealed class FakeVectorSearchService : IVectorSearchService
    {
        public IReadOnlyList<(Guid Id, float Score)> Hits { get; set; } = [];
        public void InvalidateCatalog() { }

        public Task<IReadOnlyList<(Guid Id, float Score)>> SearchAsync(
            float[] query, int k, float floor, CancellationToken cancellationToken) =>
            Task.FromResult(Hits);
    }

    private sealed class FakeKeywordSearchService : IKeywordSearchService
    {
        public IReadOnlyList<(Guid Id, double Bm25)> Hits { get; set; } = [];

        public Task<IReadOnlyList<(Guid Id, double Bm25)>> SearchAsync(
            string query, int k, CancellationToken cancellationToken) =>
            Task.FromResult(Hits);
    }

    private static LibraryCatalogEntry MakeRow(Guid id, float[]? embedding = null)
    {
        var entry = new LibraryCatalogEntry
        {
            Id = id,
            Provider = "test",
            ProviderId = id.ToString(),
            Title = id.ToString(),
            SourceUrl = $"https://example.com/{id}",
            FirstImportedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow
        };
        if (embedding is not null)
            entry.SetEmbeddingVector(embedding);
        return entry;
    }

    [Fact]
    public async Task SearchAsync_RankFirstInOneArmAbsentFromOther_BeatsMidRankInBoth_WhenRrfDictates()
    {
        // "topOfDense": rank 1 in the dense arm, absent from the keyword arm -> 1/(60+1) = 0.016393...
        // "midBoth": rank 5 in BOTH arms -> 2 * 1/(60+5) = 0.030769...
        // midBoth's combined RRF score is higher, so it must be fused ahead of topOfDense.
        var topOfDense = Guid.NewGuid();
        var midBoth = Guid.NewGuid();
        var denseFiller = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToList(); // fills dense ranks 2-4
        var keywordFiller = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList(); // fills keyword ranks 1-4

        using var testDb = new TestDatabase();
        var vector = new FakeVectorSearchService
        {
            // Dense arm order: topOfDense (rank 1), 3 filler (ranks 2-4), midBoth (rank 5).
            Hits = new[] { topOfDense }.Concat(denseFiller).Append(midBoth)
                .Select((id, i) => (id, Score: 1f - i * 0.01f)).ToList()
        };
        var keyword = new FakeKeywordSearchService
        {
            // Keyword arm order: 4 filler (ranks 1-4), midBoth (rank 5). topOfDense never appears.
            Hits = keywordFiller.Append(midBoth)
                .Select((id, i) => (id, Bm25: (double)-i)).ToList()
        };

        var service = new HybridSearchService(vector, keyword, testDb.Db);
        var results = await service.SearchAsync("query", [1f, 0f], k: 10, CancellationToken.None);

        var topOfDenseRank = results.ToList().FindIndex(r => r.Id == topOfDense);
        var midBothRank = results.ToList().FindIndex(r => r.Id == midBoth);

        Assert.True(midBothRank >= 0 && topOfDenseRank >= 0);
        Assert.True(midBothRank < topOfDenseRank, "a doc ranked mid-table in both arms should out-rank a doc ranked #1 in only one arm.");
    }

    [Fact]
    public async Task SearchAsync_KeywordOnlyHit_ComputesTrueCosineFromStoredEmbedding()
    {
        var id = Guid.NewGuid();
        var queryVector = new float[] { 1f, 0f, 0f };
        using var testDb = new TestDatabase();
        testDb.Db.LibraryCatalogEntries.Add(MakeRow(id, [1f, 0f, 0f])); // parallel to query -> cosine 1
        await testDb.Db.SaveChangesAsync();

        var vector = new FakeVectorSearchService(); // dense arm finds nothing
        var keyword = new FakeKeywordSearchService { Hits = [(id, -5.0)] };

        var service = new HybridSearchService(vector, keyword, testDb.Db);
        var results = await service.SearchAsync("query", queryVector, k: 5, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(id, result.Id);
        Assert.Equal(1f, result.Score, 4);
    }

    [Fact]
    public async Task SearchAsync_KeywordOnlyHit_NoStoredEmbedding_ScoreIsZero_NotFabricated()
    {
        var id = Guid.NewGuid();
        using var testDb = new TestDatabase();
        testDb.Db.LibraryCatalogEntries.Add(MakeRow(id)); // no embedding yet
        await testDb.Db.SaveChangesAsync();

        var vector = new FakeVectorSearchService();
        var keyword = new FakeKeywordSearchService { Hits = [(id, -5.0)] };

        var service = new HybridSearchService(vector, keyword, testDb.Db);
        var results = await service.SearchAsync("query", [1f, 0f, 0f], k: 5, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(0f, result.Score);
        Assert.True(result.RrfScore > 0);
    }

    [Fact]
    public async Task SearchAsync_ZeroK_ReturnsEmpty()
    {
        using var testDb = new TestDatabase();
        var service = new HybridSearchService(new FakeVectorSearchService(), new FakeKeywordSearchService(), testDb.Db);

        var results = await service.SearchAsync("query", [1f], k: 0, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_BothArmsEmpty_ReturnsEmpty()
    {
        using var testDb = new TestDatabase();
        var service = new HybridSearchService(new FakeVectorSearchService(), new FakeKeywordSearchService(), testDb.Db);

        var results = await service.SearchAsync("query", [1f], k: 5, CancellationToken.None);

        Assert.Empty(results);
    }
}
