using System.Net;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests;

public sealed class NovelFullTaggingServiceTests
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
    public async Task GetTagsForTitleAsync_ReturnsScrapedTagsOnMatch()
    {
        var searchHtml = """
        <div class="row">
          <div class="col-xs-7">
            <h3 class="truyen-title"><a href="/god-of-fishing.html" title="God of Fishing">God of Fishing</a></h3>
          </div>
        </div>
        """;

        var detailsHtml = """
        <div class="info">
          <h3>Genre:</h3>
          <a href="/genre/Action" title="Action">Action</a>, 
          <a href="/genre/Adventure" title="Adventure">Adventure</a>
        </div>
        """;

        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("god-of-fishing.html"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(detailsHtml) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchHtml) };
        });

        var client = new HttpClient(handler);
        var factory = new FakeHttpClientFactory(client);
        var service = new NovelFullTaggingService(factory, NullLogger<NovelFullTaggingService>.Instance);

        var result = await service.GetTagsForTitleAsync("God of Fishing", null, BookmarkTagDomain.Novel, null, CancellationToken.None);

        Assert.False(result.WasRejected);
        Assert.Equal(new[] { "Novel", "Action", "Adventure" }, result.Tags);
        Assert.Equal("God of Fishing", result.CanonicalTitle);
    }

    [Fact]
    public async Task GetTagsForTitleAsync_PunctuatedSeedTitle_SurfacesCanonicalTitle()
    {
        var searchHtml = """
        <div class="row">
          <div class="col-xs-7">
            <h3 class="truyen-title"><a href="/max-level-learning.html" title="Max-Level Learning Ability: Facing The Cliff">Max-Level Learning Ability: Facing The Cliff</a></h3>
          </div>
        </div>
        """;

        var detailsHtml = """
        <div class="info">
          <h3>Genre:</h3>
          <a href="/genre/Fantasy" title="Fantasy">Fantasy</a>
        </div>
        """;

        var handler = new MockHttpMessageHandler(req =>
        {
            if (req.RequestUri!.ToString().Contains("max-level-learning.html"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(detailsHtml) };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchHtml) };
        });

        var client = new HttpClient(handler);
        var factory = new FakeHttpClientFactory(client);
        var service = new NovelFullTaggingService(factory, NullLogger<NovelFullTaggingService>.Instance);

        var result = await service.GetTagsForTitleAsync(
            "Max-Level Learning Ability: Facing The Cliff",
            null,
            BookmarkTagDomain.Novel,
            null,
            CancellationToken.None);

        Assert.False(result.WasRejected);
        Assert.Equal("Max-Level Learning Ability: Facing The Cliff", result.CanonicalTitle);
        Assert.Contains("Novel", result.Tags);
    }

    [Fact]
    public async Task GetTagsForTitleAsync_RejectsBelowThreshold()
    {
        var searchHtml = """
        <div class="row">
          <div class="col-xs-7">
            <h3 class="truyen-title"><a href="/some-other.html" title="Some Other Novel Name">Some Other Novel Name</a></h3>
          </div>
        </div>
        """;

        var handler = new MockHttpMessageHandler(req => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchHtml) });

        var client = new HttpClient(handler);
        var factory = new FakeHttpClientFactory(client);
        var service = new NovelFullTaggingService(factory, NullLogger<NovelFullTaggingService>.Instance);

        var result = await service.GetTagsForTitleAsync("God of Fishing", null, BookmarkTagDomain.Novel, null, CancellationToken.None);

        Assert.False(result.WasRejected);
        Assert.Empty(result.Tags);
    }
}
