using System.Net;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkManager.UnitTests;

public sealed class NovelUpdatesTaggingServiceTests
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
    public async Task GetTagsForTitleAsync_StrongTitleMatchReturnsGenreAndTagFields()
    {
        var searchHtml = """
        <div class="search_main_box_nu">
          <div class="search_title"><a href="https://www.novelupdates.com/series/a-monster-who-levels-up/">A Monster Who Levels Up</a></div>
        </div>
        """;

        var seriesHtml = """
        <div id="seriesgenre">
          <a href="https://www.novelupdates.com/genre/action/">Action</a>
          <a href="https://www.novelupdates.com/genre/fantasy/">Fantasy</a>
        </div>
        <div id="showtags">
          <a href="https://www.novelupdates.com/stag/level-system/">Level System</a>
          <a href="https://www.novelupdates.com/stag/weak-to-strong/">Weak to Strong</a>
        </div>
        <div class="seriesother">Type: Korean Novel</div>
        """;

        var service = CreateService(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/series/a-monster-who-levels-up/"))
                return Html(seriesHtml);

            return Html(searchHtml);
        });

        var result = await service.GetTagsForTitleAsync("A Monster Who Levels Up", null, BookmarkTagDomain.Novel, null, CancellationToken.None);

        Assert.False(result.WasRejected);
        Assert.Equal(new[] { "Novel", "Action", "Fantasy", "Level System", "Weak to Strong", "Korean Novel" }, result.Tags);
    }

    [Fact]
    public async Task GetTagsForTitleAsync_RejectsWeakTitleMatch()
    {
        var searchHtml = """
        <div class="search_main_box_nu">
          <div class="search_title"><a href="https://www.novelupdates.com/series/completely-different-title/">Completely Different Title</a></div>
        </div>
        """;

        var detailsRequested = false;
        var service = CreateService(request =>
        {
            if (request.RequestUri!.AbsoluteUri.Contains("/series/"))
            {
                detailsRequested = true;
                return Html("<div id=\"seriesgenre\"><a>Action</a></div>");
            }

            return Html(searchHtml);
        });

        var result = await service.GetTagsForTitleAsync("A Monster Who Levels Up", null, BookmarkTagDomain.Novel, null, CancellationToken.None);

        Assert.False(detailsRequested);
        Assert.False(result.WasRejected);
        Assert.Empty(result.Tags);
        Assert.Contains("threshold", result.RejectionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetTagsForTitleAsync_DedupesTagsCaseInsensitively()
    {
        var searchHtml = """
        <div class="search_title"><a href="/series/god-of-fishing/">God of Fishing</a></div>
        """;
        var seriesHtml = """
        <div id="seriesgenre">
          <a href="/genre/action/">Action</a>
          <a href="/genre/fantasy/">Fantasy</a>
        </div>
        <div id="showtags">
          <a href="/stag/action/">action</a>
          <a href="/stag/fantasy/">FANTASY</a>
          <a href="/stag/level-system/">Level System</a>
        </div>
        """;

        var service = CreateService(request => request.RequestUri!.AbsoluteUri.Contains("/series/god-of-fishing/") ? Html(seriesHtml) : Html(searchHtml));

        var result = await service.GetTagsForTitleAsync("God of Fishing", null, BookmarkTagDomain.Novel, null, CancellationToken.None);

        Assert.Equal(new[] { "Novel", "Action", "Fantasy", "Level System" }, result.Tags);
    }

    [Fact]
    public async Task GetTagsForTitleAsync_ReturnsEmptyTagsOnNetworkFailure()
    {
        var service = CreateService(_ => throw new HttpRequestException("network unavailable"));

        var result = await service.GetTagsForTitleAsync("God of Fishing", null, BookmarkTagDomain.Novel, null, CancellationToken.None);

        Assert.False(result.WasRejected);
        Assert.Empty(result.Tags);
    }

    private static NovelUpdatesTaggingService CreateService(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        var client = new HttpClient(new MockHttpMessageHandler(handler));
        return new NovelUpdatesTaggingService(new FakeHttpClientFactory(client), NullLogger<NovelUpdatesTaggingService>.Instance);
    }

    private static HttpResponseMessage Html(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body) };
}
