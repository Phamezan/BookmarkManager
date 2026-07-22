using System.Net;
using System.Text;
using BookmarkManager.Api.Services.Library;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

/// <summary>Covers <see cref="AniListLibraryProvider.GetCatalogPageAsync"/> page-token math used by
/// <see cref="LibraryCatalogSyncBackgroundService"/>'s bulk-import crawl. No live HTTP - responses are
/// canned JSON via a mocked <see cref="HttpMessageHandler"/>, per the provider-test convention.</summary>
public sealed class AniListLibraryProviderCatalogTests
{
    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(handler(request));
        }
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private static string MediaPageJson(int count, int startId = 1)
    {
        var sb = new StringBuilder();
        sb.Append("""{ "data": { "Page": { "media": [""");
        for (var i = 0; i < count; i++)
        {
            if (i > 0) sb.Append(',');
            var id = startId + i;
            sb.Append($$"""{ "id": {{id}}, "type": "MANGA", "title": { "romaji": "Title {{id}}" } }""");
        }
        sb.Append("] } } }");
        return sb.ToString();
    }

    private static AniListLibraryProvider CreateProvider(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        var factory = new FakeHttpClientFactory(client);
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new AniListLibraryProvider(factory, cache, NullLogger<AniListLibraryProvider>.Instance);
    }

    [Fact]
    public async Task GetCatalogPageAsync_FirstPageFull_ReturnsNextPageTokenAndZeroRankBase()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(MediaPageJson(50))
        });
        var provider = CreateProvider(handler);

        var page = await provider.GetCatalogPageAsync("MANGA", null, CancellationToken.None);

        Assert.Equal(50, page.Entries.Count);
        Assert.Equal("2", page.NextContinuationToken);
        Assert.Equal(0, page.RankBase);
    }

    [Fact]
    public async Task GetCatalogPageAsync_SubsequentPage_ComputesRankBaseFromPageNumber()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(MediaPageJson(50, startId: 1000))
        });
        var provider = CreateProvider(handler);

        var page = await provider.GetCatalogPageAsync("MANGA", "3", CancellationToken.None);

        Assert.Equal("4", page.NextContinuationToken);
        Assert.Equal(100, page.RankBase); // (page 3 - 1) * 50
    }

    [Fact]
    public async Task GetCatalogPageAsync_PartialPage_ReturnsNullNextTokenSequenceExhausted()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(MediaPageJson(10))
        });
        var provider = CreateProvider(handler);

        var page = await provider.GetCatalogPageAsync("ANIME", null, CancellationToken.None);

        Assert.Equal(10, page.Entries.Count);
        Assert.Null(page.NextContinuationToken);
    }

    [Fact]
    public async Task GetCatalogPageAsync_UpstreamFailure_ThrowsHttpRequestException()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GetCatalogPageAsync("MANGA", null, CancellationToken.None));
    }

    [Fact]
    public void BuildCatalogPageBody_IncludesTypePageAndPerPageVariables()
    {
        var body = AniListLibraryProvider.BuildCatalogPageBody("MANGA", 3, 50);
        var json = System.Text.Json.JsonSerializer.Serialize(body);

        Assert.Contains("\"type\":\"MANGA\"", json);
        Assert.Contains("\"page\":3", json);
        Assert.Contains("\"perPage\":50", json);
        Assert.Contains("POPULARITY_DESC", json);
    }
}
