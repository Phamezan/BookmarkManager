using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Embedding;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

public sealed class VectorSearchServiceTests
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

    private static float[] Normalize(float[] vector)
    {
        var norm = (float)Math.Sqrt(vector.Sum(v => v * v));
        return norm == 0 ? vector : vector.Select(v => v / norm).ToArray();
    }

    private static LibraryCatalogEntry CreateEntry(string providerId, float[] vector)
    {
        var entry = new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "test",
            ProviderId = providerId,
            Title = providerId,
            SourceUrl = $"https://example.com/{providerId}",
            FirstImportedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow
        };
        entry.SetEmbeddingVector(Normalize(vector));
        return entry;
    }

    private static VectorSearchService CreateService(TestDatabase testDb) =>
        new(testDb.ScopeFactory, NullLogger<VectorSearchService>.Instance);

    [Fact]
    public async Task SearchAsync_RanksByCosineSimilarity_WithKnownVectors()
    {
        using var testDb = new TestDatabase();
        var parallel = CreateEntry("parallel", [1f, 0f, 0f]);
        var diagonal = CreateEntry("diagonal", [1f, 1f, 0f]);
        var orthogonal = CreateEntry("orthogonal", [0f, 1f, 0f]);
        testDb.Db.LibraryCatalogEntries.AddRange(parallel, diagonal, orthogonal);
        await testDb.Db.SaveChangesAsync();

        var results = await CreateService(testDb)
            .SearchAsync(Normalize([1f, 0f, 0f]), k: 3, floor: -1f, CancellationToken.None);

        Assert.Equal(3, results.Count);
        Assert.Equal(parallel.Id, results[0].Id);
        Assert.Equal(1f, results[0].Score, 4);
        Assert.Equal(diagonal.Id, results[1].Id);
        Assert.Equal(0.7071f, results[1].Score, 3); // cos 45°
        Assert.Equal(orthogonal.Id, results[2].Id);
        Assert.Equal(0f, results[2].Score, 4); // cos 90°
    }

    [Fact]
    public async Task SearchAsync_ExcludesCandidatesBelowFloor()
    {
        using var testDb = new TestDatabase();
        var near = CreateEntry("near", [1f, 0f, 0f]);
        var far = CreateEntry("far", [0f, 1f, 0f]);
        testDb.Db.LibraryCatalogEntries.AddRange(near, far);
        await testDb.Db.SaveChangesAsync();

        var results = await CreateService(testDb)
            .SearchAsync(Normalize([1f, 0f, 0f]), k: 10, floor: 0.3f, CancellationToken.None);

        var result = Assert.Single(results);
        Assert.Equal(near.Id, result.Id);
    }

    [Fact]
    public async Task SearchAsync_CapsResultsToK()
    {
        using var testDb = new TestDatabase();
        for (var i = 0; i < 5; i++)
        {
            testDb.Db.LibraryCatalogEntries.Add(CreateEntry($"e{i}", [1f, i * 0.01f, 0f]));
        }
        await testDb.Db.SaveChangesAsync();

        var results = await CreateService(testDb)
            .SearchAsync(Normalize([1f, 0f, 0f]), k: 2, floor: -1f, CancellationToken.None);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_WhenCacheHasNoEmbeddings()
    {
        using var testDb = new TestDatabase();

        var results = await CreateService(testDb)
            .SearchAsync(Normalize([1f, 0f, 0f]), k: 8, floor: 0.3f, CancellationToken.None);

        Assert.Empty(results);
    }
}
