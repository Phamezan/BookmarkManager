using System.Net;
using System.Text;
using BookmarkManager.Api.Services.Library;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

/// <summary>Covers <see cref="MangaDexLibraryProvider.GetCatalogPageAsync"/>'s two crawl sequences used
/// by <see cref="LibraryCatalogSyncBackgroundService"/>: the bounded offset-based "manga-popular" slice
/// and the unbounded createdAt-cursor "manga" walk that bypasses MangaDex's 10,000-offset ceiling. No
/// live HTTP - canned JSON via a mocked <see cref="HttpMessageHandler"/>.</summary>
public sealed class MangaDexLibraryProviderCatalogTests
{
    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public string? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            return Task.FromResult(handler(request));
        }
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static string MangaPageJson(int count, int startId = 1, DateTime? createdAtStart = null)
    {
        var createdAt = createdAtStart ?? new DateTime(2024, 1, 1);
        var sb = new StringBuilder();
        sb.Append("""{ "data": [""");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            var id = $"id-{startId + i}";
            var created = createdAt.AddDays(i).ToString("yyyy-MM-ddTHH:mm:ss");
            sb.Append($$"""
                {
                  "id": "{{id}}",
                  "attributes": {
                    "title": { "en": "Title {{id}}" },
                    "altTitles": [],
                    "createdAt": "{{created}}",
                    "tags": []
                  },
                  "relationships": []
                }
                """);
        }
        sb.Append("] }");
        return sb.ToString();
    }

    private static MangaDexLibraryProvider CreateProvider(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        var factory = new FakeHttpClientFactory(client);
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new MangaDexLibraryProvider(factory, cache, NullLogger<MangaDexLibraryProvider>.Instance);
    }

    [Fact]
    public async Task GetCatalogPageAsync_Popular_FirstFullPage_ReturnsNextOffsetAndRankBaseZero()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(MangaPageJson(100))
        });
        var provider = CreateProvider(handler);

        var page = await provider.GetCatalogPageAsync("manga-popular", null, CancellationToken.None);

        Assert.Equal(100, page.Entries.Count);
        Assert.Equal("100", page.NextContinuationToken);
        Assert.Equal(0, page.RankBase);
        Assert.Contains("offset=0", handler.LastRequestUri);
    }

    [Fact]
    public async Task GetCatalogPageAsync_Popular_PartialPage_ReturnsNullNextToken()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(MangaPageJson(40))
        });
        var provider = CreateProvider(handler);

        var page = await provider.GetCatalogPageAsync("manga-popular", "5000", CancellationToken.None);

        Assert.Equal(40, page.Entries.Count);
        Assert.Null(page.NextContinuationToken);
        Assert.Equal(5000, page.RankBase);
    }

    [Fact]
    public async Task GetCatalogPageAsync_Popular_NearOffsetCeiling_StopsEvenWithFullPage()
    {
        // PopularOffsetCeiling = 9,900; requesting from offset 9800 with a full page would compute
        // nextOffset = 9900, which is not > ceiling, so it's still allowed - but 9900 itself must stop.
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(MangaPageJson(100))
        });
        var provider = CreateProvider(handler);

        var page = await provider.GetCatalogPageAsync("manga-popular", "9900", CancellationToken.None);

        Assert.Null(page.NextContinuationToken);
    }

    [Fact]
    public async Task GetCatalogPageAsync_Exhaustive_NoToken_StartsFromEpochCursor()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(MangaPageJson(5))
        });
        var provider = CreateProvider(handler);

        await provider.GetCatalogPageAsync("manga", null, CancellationToken.None);

        Assert.Contains("createdAtSince=2000-01-01", handler.LastRequestUri);
    }

    [Fact]
    public async Task GetCatalogPageAsync_Exhaustive_FullPage_AdvancesCursorToLastItemCreatedAt()
    {
        var createdAtStart = new DateTime(2024, 3, 1);
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(MangaPageJson(100, createdAtStart: createdAtStart))
        });
        var provider = CreateProvider(handler);

        var page = await provider.GetCatalogPageAsync("manga", "2024-01-01T00:00:00", CancellationToken.None);

        var expectedLastCreatedAt = createdAtStart.AddDays(99).ToString("yyyy-MM-ddTHH:mm:ss");
        Assert.Equal(expectedLastCreatedAt, page.NextContinuationToken);
        Assert.Null(page.RankBase);
    }

    [Fact]
    public async Task GetCatalogPageAsync_Exhaustive_PartialPage_ReturnsNullNextTokenSequenceExhausted()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(MangaPageJson(3))
        });
        var provider = CreateProvider(handler);

        var page = await provider.GetCatalogPageAsync("manga", "2024-06-01T00:00:00", CancellationToken.None);

        Assert.Equal(3, page.Entries.Count);
        Assert.Null(page.NextContinuationToken);
    }
}
