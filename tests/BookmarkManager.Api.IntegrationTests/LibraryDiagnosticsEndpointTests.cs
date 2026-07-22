using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.Embedding;
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

public sealed class LibraryDiagnosticsEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly LibraryDiagnosticsFactory _factory = new();

    [Fact]
    public async Task Embedding_NoParams_ReturnsCoverageCounts()
    {
        await SeedAsync("Mother of Learning", embedded: true);
        await SeedAsync("The Wandering Inn", embedded: false);

        var body = await GetAsync("/api/library/diagnostics/embedding");

        Assert.True(body.ModelReady);
        Assert.Equal(2, body.TotalCount);
        Assert.Equal(1, body.EmbeddedCount);
        Assert.Equal(50d, body.EmbeddedPercent);
        Assert.Null(body.Title);
        Assert.Null(body.QueryMatches);
        Assert.Null(body.TitleRank);
    }

    [Fact]
    public async Task Embedding_WithTitle_ReportsMatchAndEmbeddedState()
    {
        await SeedAsync("Mother of Learning", embedded: true, hash: "abc123");

        var body = await GetAsync("/api/library/diagnostics/embedding?title=mother of learning");

        Assert.NotNull(body.Title);
        Assert.True(body.Title!.Found);
        Assert.Equal("Mother of Learning", body.Title.MatchedTitle);
        Assert.True(body.Title.Embedded);
        Assert.Equal("abc123", body.Title.EmbeddingSourceHash);
    }

    [Fact]
    public async Task Embedding_WithTitleMatchingAlternateTitle_ReportsFound()
    {
        await SeedAsync("Mother of Learning", embedded: false, alternateTitles: "MoL,Zorian");

        var body = await GetAsync("/api/library/diagnostics/embedding?title=zorian");

        Assert.NotNull(body.Title);
        Assert.True(body.Title!.Found);
        Assert.Equal("Mother of Learning", body.Title.MatchedTitle);
        Assert.False(body.Title.Embedded);
    }

    [Fact]
    public async Task Embedding_WithUnknownTitle_ReportsNotFound()
    {
        await SeedAsync("Mother of Learning", embedded: true);

        var body = await GetAsync("/api/library/diagnostics/embedding?title=nonexistent series");

        Assert.NotNull(body.Title);
        Assert.False(body.Title!.Found);
        Assert.Null(body.Title.MatchedTitle);
    }

    [Fact]
    public async Task Embedding_WithQueryAndTitle_ReturnsMatchesAndTitleRank()
    {
        var loId = await SeedAsync("Mother of Learning", embedded: true);
        var innId = await SeedAsync("The Wandering Inn", embedded: true);

        // Ranked worse-to-... the wide probe should place Mother of Learning at rank 2, above floor.
        _factory.Vector.Ranked = [(loId, 0.9f), (innId, 0.4f)];

        var body = await GetAsync("/api/library/diagnostics/embedding?title=mother&query=time loop novel");

        Assert.NotNull(body.QueryMatches);
        Assert.Equal(2, body.QueryMatches!.Count);
        Assert.Equal("Mother of Learning", body.QueryMatches[0].Title);
        Assert.Equal(0.9f, body.QueryMatches[0].Score);

        Assert.NotNull(body.TitleRank);
        Assert.Equal("Mother of Learning", body.TitleRank!.MatchedTitle);
        Assert.Equal(1, body.TitleRank.Rank);
        Assert.Equal(0.9f, body.TitleRank.Score);
        Assert.True(body.TitleRank.AboveFloor);
    }

    [Fact]
    public async Task Embedding_TitleBelowFloor_StillRankedButNotAboveFloor()
    {
        var loId = await SeedAsync("Mother of Learning", embedded: true);
        var innId = await SeedAsync("The Wandering Inn", embedded: true);

        // Mother of Learning scores below RagMinSimilarity (0.3) - retrieval drops it, the wide probe keeps it.
        _factory.Vector.Ranked = [(innId, 0.8f), (loId, 0.1f)];

        var body = await GetAsync("/api/library/diagnostics/embedding?title=mother&query=cooking story");

        Assert.NotNull(body.TitleRank);
        Assert.Equal(2, body.TitleRank!.Rank);
        Assert.Equal(0.1f, body.TitleRank.Score);
        Assert.False(body.TitleRank.AboveFloor);
    }

    [Fact]
    public async Task Embedding_ModelNotReady_ReturnsCountsWithoutQuery()
    {
        _factory.Embedding.Ready = false;
        await SeedAsync("Mother of Learning", embedded: true);

        var body = await GetAsync("/api/library/diagnostics/embedding?query=anything");

        Assert.False(body.ModelReady);
        Assert.Equal(1, body.TotalCount);
        Assert.Equal(1, body.EmbeddedCount);
        Assert.Null(body.QueryMatches);
        Assert.Null(body.TitleRank);
    }

    private async Task<LibraryEmbeddingDiagnosticDto> GetAsync(string url)
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LibraryEmbeddingDiagnosticDto>(JsonOptions);
        Assert.NotNull(body);
        return body!;
    }

    private async Task<Guid> SeedAsync(
        string title, bool embedded, string? hash = null, string? alternateTitles = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "CatalogProvider",
            ProviderId = Guid.NewGuid().ToString(),
            Title = title,
            AlternateTitles = alternateTitles,
            MediaType = LibraryMediaType.Webnovel,
            SourceUrl = "https://example.com/" + Guid.NewGuid().ToString("N"),
            FirstImportedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow,
            EmbeddingSourceHash = embedded ? hash : null
        };
        if (embedded)
        {
            var vector = new float[EmbeddingConstants.EmbeddingDimensions];
            vector[0] = 1f;
            entry.SetEmbeddingVector(vector);
        }
        db.LibraryCatalogEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    public void Dispose() => _factory.Dispose();

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public bool Ready { get; set; } = true;
        public bool IsReady => Ready;

        public Task<float[]> EmbedQueryAsync(string text, CancellationToken cancellationToken) => EmbedAsync(text, cancellationToken);


        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
        {
            var vector = new float[EmbeddingConstants.EmbeddingDimensions];
            vector[0] = 1f;
            return Task.FromResult(vector);
        }

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<float[]>>(texts.Select(_ => new float[EmbeddingConstants.EmbeddingDimensions]).ToList());
    }

    private sealed class FakeVectorSearchService : IVectorSearchService
    {
        /// <summary>Full ranked list (highest score first); SearchAsync honors k + floor over it.</summary>
        public IReadOnlyList<(Guid Id, float Score)> Ranked { get; set; } = [];

        public void InvalidateCatalog() { }

        public Task<IReadOnlyList<(Guid Id, float Score)>> SearchAsync(float[] query, int k, float floor, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<(Guid, float)>>(
                Ranked.Where(r => r.Score >= floor).Take(k).ToList());
    }

    private sealed class LibraryDiagnosticsFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bm-lib-diag-{Guid.NewGuid():N}.db");
        private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"bm-diag-data-{Guid.NewGuid():N}");

        public FakeVectorSearchService Vector { get; } = new();
        public FakeEmbeddingService Embedding { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = $"Data Source={_dbPath}",
                    ["Backup:Directory"] = Path.Combine(Path.GetTempPath(), $"bm-diag-backups-{Guid.NewGuid():N}"),
                    ["Backup:Enabled"] = "false",
                    ["Backup:StopHostAfterRestore"] = "false"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_dbPath}"));

                services.RemoveAll<IEmbeddingService>();
                services.AddSingleton<IEmbeddingService>(Embedding);
                services.RemoveAll<IVectorSearchService>();
                services.AddSingleton<IVectorSearchService>(Vector);

                services.RemoveAll<AiTaggingSettingsService>();
                Directory.CreateDirectory(_dataDir);
                services.AddSingleton<AiTaggingSettingsService>(
                    new TestAiTaggingSettingsService(Path.Combine(_dataDir, "ai-tagging-settings.json")));

                using var sp = services.BuildServiceProvider();
                using var scope = sp.CreateScope();
                scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureDeleted();
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
                // Best-effort cleanup.
            }
        }
    }
}
