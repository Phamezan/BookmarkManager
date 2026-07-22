using System.Net;
using System.Net.Http.Json;
using System.Text;
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
        _factory.Vector.Hits = [(entryId, 0.87f)];
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

    private async Task<Guid> SeedCatalogEntryAsync(string title, string synopsis)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var entry = new LibraryCatalogEntry
        {
            Id = Guid.NewGuid(),
            Provider = "CatalogProvider",
            ProviderId = "1",
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
