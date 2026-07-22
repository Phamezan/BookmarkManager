using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.Embedding;
using BookmarkManager.Api.Services.Rerank;
using BookmarkManager.Api.Services.Search;
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

public sealed class LibraryRagEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly LibraryRagEndpointFactory _factory = new();

    [Fact]
    public async Task Chat_WithMockedEmbeddingAndLlm_Returns200WithMarkdownAndSeriesCards()
    {
        var entryId = await SeedCatalogEntryAsync("Mother of Learning", "A time-loop progression fantasy.");
        _factory.Hybrid.Hits = [(entryId, 0.87f, 1.0)];
        await SetRagApiKeyAsync("test-rag-key");

        using var client = _factory.CreateClient();
        var request = new LibraryChatRequestDto("Recommend a time loop novel");
        using var response = await client.PostAsJsonAsync("/api/library/chat", request, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<LibraryChatResponseDto>(JsonOptions);
        Assert.NotNull(body);
        Assert.False(string.IsNullOrWhiteSpace(body!.Markdown));
        Assert.Contains("Mother of Learning", body.Markdown);

        var card = Assert.Single(body.Series);
        Assert.Equal("Mother of Learning", card.Title);
        Assert.Equal(0.87f, card.Score);
    }

    [Fact]
    public async Task Chat_WithEmptyMessage_Returns400()
    {
        using var client = _factory.CreateClient();
        var request = new LibraryChatRequestDto("   ");
        using var response = await client.PostAsJsonAsync("/api/library/chat", request, JsonOptions);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Chat_WithRerankerReady_ReturnsRerankOrder_NotHybridOrder()
    {
        var lowId = await SeedCatalogEntryAsync("Shared Vocabulary Story", "A story that shares words with the query but isn't the answer.");
        var highId = await SeedCatalogEntryAsync("Shadow Monarch Ascendant", "The true match for the query.");

        // Hybrid (stage-1) ranks lowId first, highId second - the reranker (stage-2) scores the opposite
        // way, so the final response order proves the endpoint actually applied the rerank, not just
        // passed the hybrid order through.
        _factory.Hybrid.Hits = [(lowId, 0.9f, 2.0), (highId, 0.1f, 1.0)];
        _factory.Reranker.IsReady = true;
        _factory.Reranker.ScoreFunc = (_, passages) => passages
            .Select(p => p.Contains("true match", StringComparison.Ordinal) ? 5f : 0f)
            .ToArray();
        await SetRagApiKeyAsync("test-rag-key");

        using var client = _factory.CreateClient();
        var request = new LibraryChatRequestDto("shadow monarch");
        using var response = await client.PostAsJsonAsync("/api/library/chat", request, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LibraryChatResponseDto>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(2, body!.Series.Count);
        Assert.Equal("Shadow Monarch Ascendant", body.Series[0].Title);
        Assert.Equal("Shared Vocabulary Story", body.Series[1].Title);
    }

    [Fact]
    public async Task Chat_WithRerankerNotReady_FallsBackToHybridOrderUnchanged()
    {
        var firstId = await SeedCatalogEntryAsync("First Hybrid Result", "Top of the hybrid ranking.");
        var secondId = await SeedCatalogEntryAsync("Second Hybrid Result", "Second in the hybrid ranking.");

        _factory.Hybrid.Hits = [(firstId, 0.9f, 2.0), (secondId, 0.5f, 1.0)];
        _factory.Reranker.IsReady = false;
        await SetRagApiKeyAsync("test-rag-key");

        using var client = _factory.CreateClient();
        var request = new LibraryChatRequestDto("anything");
        using var response = await client.PostAsJsonAsync("/api/library/chat", request, JsonOptions);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<LibraryChatResponseDto>(JsonOptions);
        Assert.NotNull(body);
        Assert.Equal(2, body!.Series.Count);
        Assert.Equal("First Hybrid Result", body.Series[0].Title);
        Assert.Equal("Second Hybrid Result", body.Series[1].Title);
    }

    private async Task<Guid> SeedCatalogEntryAsync(string title, string synopsis)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "CatalogProvider",
            ProviderId = Guid.NewGuid().ToString("N"),
            Title = title,
            Synopsis = synopsis,
            Genres = "Fantasy,Progression",
            MediaType = LibraryMediaType.Webnovel,
            SourceUrl = "https://example.com/1",
            FirstImportedAt = DateTimeOffset.UtcNow,
            LastRefreshedAt = DateTimeOffset.UtcNow
        };
        db.LibraryCatalogEntries.Add(entry);
        await db.SaveChangesAsync();
        return entry.Id;
    }

    private async Task SetRagApiKeyAsync(string key)
    {
        using var scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<AiTaggingSettingsService>();
        var current = await settings.GetAsync(CancellationToken.None);
        current.RagApiKey = key;
        await settings.SaveAsync(current, CancellationToken.None);
    }

    public void Dispose() => _factory.Dispose();

    private sealed class FakeEmbeddingService : IEmbeddingService
    {
        public bool IsReady => true;

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
        public IReadOnlyList<(Guid Id, float Score)> Hits { get; set; } = [];

        public void InvalidateCatalog() { }

        public Task<IReadOnlyList<(Guid Id, float Score)>> SearchAsync(float[] query, int k, float floor, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<(Guid, float)>>(Hits.Take(k).ToList());
    }

    private sealed class FakeHybridSearchService : IHybridSearchService
    {
        public IReadOnlyList<(Guid Id, float Score, double RrfScore)> Hits { get; set; } = [];

        public Task<IReadOnlyList<(Guid Id, float Score, double RrfScore)>> SearchAsync(
            string queryText, float[] queryVector, int k, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<(Guid, float, double)>>(Hits.Take(k).ToList());
    }

    private sealed class FakeRerankerService : IRerankerService
    {
        public bool IsReady { get; set; }

        public Func<string, IReadOnlyList<string>, IReadOnlyList<float>>? ScoreFunc { get; set; }

        public Task<IReadOnlyList<float>> ScoreAsync(string query, IReadOnlyList<string> passages, CancellationToken cancellationToken)
        {
            if (!IsReady)
                throw new InvalidOperationException("Reranker model is not ready.");
            var scores = ScoreFunc?.Invoke(query, passages) ?? passages.Select(_ => 0f).ToArray();
            return Task.FromResult(scores);
        }
    }

    private sealed class StubLlmHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            const string json = """
                {"choices":[{"message":{"role":"assistant","content":"You might enjoy **Mother of Learning**, a time-loop progression fantasy."}}]}
                """;
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class LibraryRagEndpointFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bm-library-rag-{Guid.NewGuid():N}.db");
        private readonly string _dataDir = Path.Combine(Path.GetTempPath(), $"bm-rag-data-{Guid.NewGuid():N}");

        public FakeVectorSearchService Vector { get; } = new();
        public FakeHybridSearchService Hybrid { get; } = new();
        public FakeRerankerService Reranker { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Default"] = $"Data Source={_dbPath}",
                    ["Backup:Directory"] = Path.Combine(Path.GetTempPath(), $"bm-backups-{Guid.NewGuid():N}"),
                    ["Backup:Enabled"] = "false",
                    ["Backup:StopHostAfterRestore"] = "false"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_dbPath}"));

                // Mock the embedding model and vector search so no ONNX/model download happens.
                services.RemoveAll<IEmbeddingService>();
                services.AddSingleton<IEmbeddingService, FakeEmbeddingService>();
                services.RemoveAll<IVectorSearchService>();
                services.AddSingleton<IVectorSearchService>(Vector);
                services.RemoveAll<IHybridSearchService>();
                services.AddScoped<IHybridSearchService>(_ => Hybrid);
                services.RemoveAll<IRerankerService>();
                services.AddSingleton<IRerankerService>(Reranker);

                // Isolate settings to a temp dir and stub the LLM HTTP call.
                services.RemoveAll<AiTaggingSettingsService>();
                Directory.CreateDirectory(_dataDir);
                services.AddSingleton<AiTaggingSettingsService>(
                    new TestAiTaggingSettingsService(Path.Combine(_dataDir, "ai-tagging-settings.json")));

                services.AddHttpClient(BookmarkManager.Api.Services.Rag.LibraryRagService.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new StubLlmHandler());

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
