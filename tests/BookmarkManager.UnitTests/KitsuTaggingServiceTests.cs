using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests;

public sealed class KitsuTaggingServiceTests
{
    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    [Fact]
    public async Task GetTagsForTitleAsync_ReturnsExpectedTagsOnMatch()
    {
        var searchJson = """
        {
          "data": [
            {
              "id": "12345",
              "attributes": {
                "canonicalTitle": "God of Fishing",
                "subtype": "novel"
              }
            }
          ]
        }
        """;

        var categoriesJson = """
        {
          "data": [
            {
              "attributes": {
                "title": "Action"
              }
            },
            {
              "attributes": {
                "title": "Fantasy"
              }
            }
          ]
        }
        """;

        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("categories"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(categoriesJson) { Headers = { ContentType = new MediaTypeHeaderValue("application/vnd.api+json") } }
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(searchJson) { Headers = { ContentType = new MediaTypeHeaderValue("application/vnd.api+json") } }
            };
        });

        var client = new HttpClient(handler);
        var factory = new FakeHttpClientFactory(client);
        var service = new KitsuTaggingService(factory, NullLogger<KitsuTaggingService>.Instance);

        var result = await service.GetTagsForTitleAsync("God of Fishing", null, BookmarkTagDomain.Novel, null, CancellationToken.None);

        Assert.False(result.WasRejected);
        Assert.Equal(new[] { "Novel", "Action", "Fantasy" }, result.Tags);
    }

    [Fact]
    public async Task GetTagsForTitleAsync_RejectsBelowThreshold()
    {
        var searchJson = """
        {
          "data": [
            {
              "id": "12345",
              "attributes": {
                "canonicalTitle": "Completely Different Novel Title",
                "subtype": "novel"
              }
            }
          ]
        }
        """;

        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(searchJson) { Headers = { ContentType = new MediaTypeHeaderValue("application/vnd.api+json") } }
        });

        var client = new HttpClient(handler);
        var factory = new FakeHttpClientFactory(client);
        var service = new KitsuTaggingService(factory, NullLogger<KitsuTaggingService>.Instance);

        var result = await service.GetTagsForTitleAsync("God of Fishing", null, BookmarkTagDomain.Novel, null, CancellationToken.None);

        Assert.False(result.WasRejected);
        Assert.Empty(result.Tags);
    }

    [Fact]
    public async Task GetTagsForTitleAsync_AnimeDomainSearchesAnimeEndpoint()
    {
        var requestedUris = new List<string>();
        var searchJson = """
        {
          "data": [
            {
              "id": "999",
              "attributes": {
                "canonicalTitle": "Naruto Shippuden",
                "subtype": "TV"
              }
            }
          ]
        }
        """;

        var categoriesJson = """
        {
          "data": [
            {
              "attributes": {
                "title": "Action"
              }
            }
          ]
        }
        """;

        var handler = new MockHttpMessageHandler(req =>
        {
            requestedUris.Add(req.RequestUri!.ToString());
            var content = req.RequestUri!.ToString().Contains("categories", StringComparison.Ordinal)
                ? categoriesJson
                : searchJson;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content) { Headers = { ContentType = new MediaTypeHeaderValue("application/vnd.api+json") } }
            };
        });

        var client = new HttpClient(handler);
        var factory = new FakeHttpClientFactory(client);
        var service = new KitsuTaggingService(factory, NullLogger<KitsuTaggingService>.Instance);

        var result = await service.GetTagsForTitleAsync("Naruto Shippuden Episode 42", null, BookmarkTagDomain.Anime, null, CancellationToken.None);

        Assert.False(result.WasRejected);
        Assert.Equal(new[] { "Anime", "Action" }, result.Tags);
        Assert.Contains(requestedUris, uri => uri.StartsWith("https://kitsu.io/api/edge/anime?", StringComparison.Ordinal));
        Assert.Contains(requestedUris, uri => uri.StartsWith("https://kitsu.io/api/edge/anime/999/categories", StringComparison.Ordinal));
        Assert.DoesNotContain(requestedUris, uri => uri.Contains("/api/edge/manga", StringComparison.Ordinal));
    }
}
