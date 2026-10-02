using System.Net;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.UrlMigration;
using BookmarkManager.UnitTests.UrlMigration.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkManager.UnitTests.UrlMigration;

public sealed class DuckDuckGoSearchTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UrlMigration", "Fixtures", name));

    [Theory]
    [InlineData("ddg-results.html", false)]
    [InlineData("yahoo-results.html", true)]
    public void ParsesRedirectAndDirectResults_ExcludesDeadHostNoiseAndNavigation(string fixture, bool yahoo)
    {
        var results = DuckDuckGoSearchService.ParseResults(Fixture(fixture), "webtoon.xyz", yahoo);
        Assert.Equal(2, results.Count);
        Assert.Contains(results, url => new Uri(url).Host == "asuracomic.net");
        Assert.Contains(results, url => new Uri(url).Host == "mangadex.org");
        Assert.DoesNotContain(results, url => url.Contains("amp;"));
    }

    [Fact]
    public async Task ChallengeFallsBackToYahoo_AndCircuitsSkipBothProviders()
    {
        var ddgCalls = 0;
        var yahooCalls = 0;
        var factory = new RoutingHttpClientFactory(new Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>>
        {
            ["DuckDuckGoTriage"] = _ => { ddgCalls++; return new(HttpStatusCode.OK) { Content = new StringContent(Fixture("ddg-challenge.html")) }; },
            ["YahooTriage"] = _ => { yahooCalls++; return new(HttpStatusCode.InternalServerError); }
        });
        var service = new DuckDuckGoSearchService(factory, NullLogger<DuckDuckGoSearchService>.Instance);
        var run = new SearchRunContext();
        SearchOutcome<string>? result = null;
        for (var i = 0; i < 5; i++) result = await service.SearchWithDiagnosticsAsync("Academy Player chapter 51", "webtoon.xyz", run, default);
        Assert.Equal(3, ddgCalls);
        Assert.Equal(3, yahooCalls);
        Assert.Equal("DuckDuckGo: bot challenge (skipped for this run); Yahoo: HTTP 500 (skipped for this run)", result!.Detail);
    }

    [Fact]
    public async Task BlockedDdg_YahooFixtureYieldsRealCandidates()
    {
        var factory = new RoutingHttpClientFactory(new Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>>
        {
            ["DuckDuckGoTriage"] = _ => new(HttpStatusCode.Forbidden),
            ["YahooTriage"] = _ => new(HttpStatusCode.OK) { Content = new StringContent(Fixture("yahoo-results.html")) }
        });
        var service = new DuckDuckGoSearchService(factory, NullLogger<DuckDuckGoSearchService>.Instance);
        var result = await service.SearchWithDiagnosticsAsync("Academy Player chapter 51", "webtoon.xyz", new(), default);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Equal("DuckDuckGo: HTTP 403; Yahoo: 2 results", result.Detail);
    }
}
