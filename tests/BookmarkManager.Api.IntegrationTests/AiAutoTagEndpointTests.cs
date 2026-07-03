using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace BookmarkManager.Api.IntegrationTests;

public sealed class AiAutoTagEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AiAutoTagEndpointFactory _factory = new();

    [Fact]
    public async Task AiAutoTagAsync_InvalidFolderReturnsNotFound()
    {
        using var client = _factory.CreateClient();
        var missingId = Guid.NewGuid();

        using var response = await client.PostAsync($"/api/bookmarks/{missingId}/ai-auto-tag", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AiAutoTagAsync_ValidFolderReturnsSummaryAndSavesTagsWithoutExtensionCommands()
    {
        using var client = _factory.CreateClient();
        var folderId = Guid.NewGuid();
        var bookmarkId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BookmarkNodes.AddRange(
                new BookmarkNode
                {
                    Id = folderId,
                    Type = NodeType.Folder,
                    Title = "Anime",
                    Position = 0,
                    Version = 1,
                    UpdatedAt = DateTime.UtcNow
                },
                new BookmarkNode
                {
                    Id = bookmarkId,
                    ParentId = folderId,
                    Type = NodeType.Bookmark,
                    Title = "One Piece Episode 1092",
                    Url = "https://crunchyroll.com/watch/one-piece",
                    Position = 0,
                    SyncState = SyncState.Synced,
                    Version = 1,
                    UpdatedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync();
        }
        _factory.Identifier.EnqueueIdentification(bookmarkId, "One Piece", 0.91, AiSeriesSourceHint.Anime);
        _factory.Anilist.SetTags("One Piece", ["Shounen"]);

        using var response = await client.PostAsync($"/api/bookmarks/{folderId}/ai-auto-tag?forceRefresh=false", null);

        response.EnsureSuccessStatusCode();
        var summary = await response.Content.ReadFromJsonAsync<AiAutoTagSummaryDto>(Options);
        Assert.NotNull(summary);
        Assert.Equal(1, summary!.TotalCandidates);
        Assert.Equal(1, summary.Tagged);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bookmark = await verifyDb.BookmarkNodes.SingleAsync(node => node.Id == bookmarkId);
        Assert.Equal("Anime,Shounen", bookmark.Tags);
        Assert.Empty(await verifyDb.ExtensionCommands.ToListAsync());
    }

    public void Dispose() => _factory.Dispose();

    private sealed class AiAutoTagEndpointFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"bm-ai-auto-tag-{Guid.NewGuid():N}.db");

        public QueueHttpMessageHandler Identifier { get; } = new();
        public FakeProvider Anilist { get; } = new();
        public FakeProvider MangaUpdates { get; } = new();
        public FakeProvider Kitsu { get; } = new();
        public FakeProvider NovelFull { get; } = new();
        public FakeProvider NovelUpdates { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_dbPath}"));

                services.RemoveAll<AiSeriesIdentifierService>();
                services.AddScoped(_ => new AiSeriesIdentifierService(new HttpClient(Identifier), new Uri("https://ai.local/identify")));

                services.RemoveAll<IAnilistTagProvider>();
                services.RemoveAll<IMangaUpdatesTagProvider>();
                services.RemoveAll<IKitsuTagProvider>();
                services.RemoveAll<INovelFullTagProvider>();
                services.RemoveAll<INovelUpdatesTagProvider>();
                services.AddSingleton<IAnilistTagProvider>(Anilist);
                services.AddSingleton<IMangaUpdatesTagProvider>(MangaUpdates);
                services.AddSingleton<IKitsuTagProvider>(Kitsu);
                services.AddSingleton<INovelFullTagProvider>(NovelFull);
                services.AddSingleton<INovelUpdatesTagProvider>(NovelUpdates);

                using var sp = services.BuildServiceProvider();
                using var scope = sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Database.EnsureDeleted();
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
                // Best-effort cleanup; the temp file lives in the OS temp directory.
            }
        }
    }

    private sealed class FakeProvider : IAnilistTagProvider, IMangaUpdatesTagProvider, IKitsuTagProvider, INovelFullTagProvider, INovelUpdatesTagProvider
    {
        private readonly Dictionary<string, List<string>> _tagsByTitle = new(StringComparer.OrdinalIgnoreCase);

        public void SetTags(string title, List<string> tags) => _tagsByTitle[title] = tags;

        public Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken)
            => Task.FromResult(new ProviderTagResult(
                _tagsByTitle.TryGetValue(context.OriginalTitle, out var tags) ? tags : [],
                WasRejected: false,
                RejectionReason: null));
    }

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<Guid, AiSeriesIdentification> _items = new();

        public void EnqueueIdentification(Guid id, string canonicalTitle, double confidence, AiSeriesSourceHint sourceHint)
            => _items[id] = new AiSeriesIdentification(id, canonicalTitle, confidence, sourceHint);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var requestJson = await request.Content!.ReadAsStringAsync(cancellationToken);
            var sentIds = System.Text.RegularExpressions.Regex.Matches(
                    requestJson,
                    "\\\"id\\\"\\s*:\\s*\\\"(?<id>[^\\\"]+)\\\"")
                .Select(match => Guid.Parse(match.Groups["id"].Value))
                .ToList();
            var items = sentIds.Select(id =>
            {
                var item = _items[id];
                return $"{{ \"id\": \"{item.Id}\", \"canonicalTitle\": \"{item.CanonicalTitle}\", \"confidence\": {item.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture)}, \"sourceHint\": \"{item.SourceHint}\" }}";
            });

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{ \"items\": [ {string.Join(",", items)} ] }}")
            };
        }
    }
}
