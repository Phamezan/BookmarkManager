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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests.Search;

/// <summary>End-to-end proof, against a real (migrated) SQLite database with the real
/// <see cref="VectorSearchService"/>, <see cref="FtsKeywordSearchService"/> and
/// <see cref="HybridSearchService"/> wired together, that the keyword arm actually changes the
/// retrieval outcome: an exact proper-noun query surfaces a row whose embedding is deliberately
/// unrelated to the query vector, which pure dense-vector top-k retrieval alone would drop.</summary>
public sealed class HybridSearchEndToEndTests
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
            Db.Database.Migrate();

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

    private static float[] Normalize(float[] vector)
    {
        var norm = (float)Math.Sqrt(vector.Sum(v => v * v));
        return norm == 0 ? vector : vector.Select(v => v / norm).ToArray();
    }

    private static LibraryCatalogEntry MakeRow(string title, string synopsis, float[] embedding)
    {
        var entry = new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "test",
            ProviderId = Guid.NewGuid().ToString(),
            Title = title,
            Synopsis = synopsis,
            SourceUrl = $"https://example.com/{Guid.NewGuid():N}",
            FirstImportedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow
        };
        entry.SetEmbeddingVector(Normalize(embedding));
        return entry;
    }

    [Fact]
    public async Task SearchAsync_ExactProperNounQuery_SurfacesRow_ThatPureVectorSearchWouldDrop()
    {
        using var testDb = new TestDatabase();

        // The query vector points at [1, 0, 0]. Several "decoy" rows are embedded very close to it so
        // pure top-k dense retrieval fills up on them - none mention the proper noun the user asked
        // about. The target row's title contains the exact proper noun, but its embedding is
        // deliberately orthogonal to the query (cosine ~0) - a semantically unrelated row by design.
        var queryVector = Normalize([1f, 0f, 0f]);
        var target = MakeRow(
            "Zylquorath the Undying",
            "An ancient chronicle of an unrelated cooking competition.",
            embedding: [0f, 1f, 0f]); // orthogonal to the query vector -> cosine ~0

        var decoys = Enumerable.Range(0, 5)
            .Select(i => MakeRow($"Decoy Series {i}", "Nothing to do with the query.", [1f, 0.001f * i, 0f]))
            .ToList();

        testDb.Db.LibraryCatalogEntries.AddRange(decoys);
        testDb.Db.LibraryCatalogEntries.Add(target);
        await testDb.Db.SaveChangesAsync();

        var vectorSearch = new VectorSearchService(testDb.ScopeFactory, NullLogger<VectorSearchService>.Instance);
        var keywordSearch = new FtsKeywordSearchService(testDb.Db);
        var hybridSearch = new HybridSearchService(vectorSearch, keywordSearch, testDb.Db);

        const int k = 3;

        // Pure dense retrieval at the same k: the decoys crowd out the semantically-unrelated target.
        var denseOnly = await vectorSearch.SearchAsync(queryVector, k, floor: -1f, CancellationToken.None);
        Assert.DoesNotContain(denseOnly, h => h.Id == target.Id);

        // Hybrid retrieval, searching for the exact proper noun in the title: the keyword arm pulls the
        // target row in and RRF ranks it into the top k even though the dense arm alone would not.
        var hybrid = await hybridSearch.SearchAsync("Zylquorath", queryVector, k, CancellationToken.None);
        Assert.Contains(hybrid, h => h.Id == target.Id);
    }
}
