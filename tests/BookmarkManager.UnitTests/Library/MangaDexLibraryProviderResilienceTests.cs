using System.Net;
using BookmarkManager.Api.Services.Library;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

public sealed class MangaDexLibraryProviderResilienceTests
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

    private const string SearchJson = """
    {
      "data": [
        {
          "id": "a1c7c817-4e59-43b7-9365-09675a149a6f",
          "attributes": {
            "title": { "en": "Solo Leveling" },
            "altTitles": [],
            "status": "completed",
            "lastChapter": "200",
            "originalLanguage": "ko",
            "tags": []
          },
          "relationships": []
        }
      ]
    }
    """;

    [Fact]
    public async Task SearchAsync_ReturnsParsedEntryFromLiveCall()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SearchJson) });
        var provider = CreateProvider(handler);

        var results = await provider.SearchAsync("Solo Leveling", null, CancellationToken.None);

        var entry = Assert.Single(results);
        Assert.Equal("Solo Leveling", entry.Title);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SearchAsync_SecondCallServedFromCacheWithoutHittingNetwork()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(SearchJson) });
        var provider = CreateProvider(handler);

        await provider.SearchAsync("Solo Leveling", null, CancellationToken.None);
        await provider.SearchAsync("Solo Leveling", null, CancellationToken.None);

        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SearchAsync_DegradesToEmptyListWhenUpstreamFails()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var provider = CreateProvider(handler);

        var results = await provider.SearchAsync("Solo Leveling", null, CancellationToken.None);

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsync_CircuitOpensAfterRepeatedFailuresAndSkipsFurtherCalls()
    {
        var handler = new MockHttpMessageHandler(_ => throw new HttpRequestException("boom"));
        var provider = CreateProvider(handler);

        // Each call: MaxAttempts=2 retries internally, then breaker records one failure.
        // Three distinct queries (different cache keys) trip the 3-failure threshold.
        await provider.SearchAsync("Query One", null, CancellationToken.None);
        await provider.SearchAsync("Query Two", null, CancellationToken.None);
        await provider.SearchAsync("Query Three", null, CancellationToken.None);

        var callsBeforeOpen = handler.CallCount;
        var results = await provider.SearchAsync("Query Four", null, CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(callsBeforeOpen, handler.CallCount);
    }

    private static MangaDexLibraryProvider CreateProvider(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        var factory = new FakeHttpClientFactory(client);
        var cache = new MemoryCache(new MemoryCacheOptions());
        return new MangaDexLibraryProvider(factory, cache, NullLogger<MangaDexLibraryProvider>.Instance);
    }
}
