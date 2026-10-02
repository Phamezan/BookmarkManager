using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services.UrlMigration;
using BookmarkManager.UnitTests.UrlMigration.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BookmarkManager.UnitTests.UrlMigration;

public sealed class SearxngSearchServiceTests
{
    private static readonly SeriesExtraction Extraction = new("Solo Leveling", "112", "manhwa", false);

    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UrlMigration", "Fixtures", "searxng-results.json"));

    private static SearxngSearchService CreateService(HttpMessageHandler handler, string baseUrl = "http://searxng:8080/") =>
        new(new SingleClientFactory(new HttpClient(handler)),
            Options.Create(new UrlMigrationOptions { SearxngBaseUrl = baseUrl }),
            NullLogger<SearxngSearchService>.Instance);

    [Fact]
    public void ParseResults_MapsUrlTitleAndSnippet_AndSkipsMalformedEntries()
    {
        var parsed = SearxngSearchService.ParseResults("""
            {
              "results": [
                { "url": "https://asuracomic.net/series/solo-leveling/chapter-112", "title": "Asura", "content": "reader" },
                { "url": "  ", "title": "blank" },
                "not-an-object",
                { "title": "no url" }
              ]
            }
            """);

        var candidate = Assert.Single(parsed);
        Assert.Equal("https://asuracomic.net/series/solo-leveling/chapter-112", candidate.Url);
        Assert.Equal("Asura", candidate.Title);
        Assert.Equal("reader", candidate.Snippet);
    }

    [Fact]
    public void ParseResults_ReturnsEmpty_WhenResultsArrayMissing()
    {
        Assert.Empty(SearxngSearchService.ParseResults("""{ "query": "x", "answers": [] }"""));
    }

    [Fact]
    public async Task RequestShape_UsesJsonFormatAndFiltersDeadHostNoise()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, Fixture());
        });

        var result = await CreateService(handler).SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.NotNull(captured);
        Assert.Equal("GET", captured!.Method.Method);
        Assert.Equal("/search", captured.RequestUri!.AbsolutePath);
        Assert.Contains("format=json", captured.RequestUri.Query);
        // Series + media type + chapter, plus the useful category/language filters.
        var query = Uri.UnescapeDataString(captured.RequestUri.Query).Replace('+', ' ');
        Assert.Contains("q=Solo Leveling manhwa chapter 112", query);
        Assert.Contains("categories=general", query);
        Assert.Contains("language=en", query);
        Assert.Equal("http://searxng:8080/search", captured.RequestUri.GetLeftPart(UriPartial.Path));

        // Dead host, noise host, and the private IP are filtered; the two reader pages survive.
        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Candidates, c => c.Url == "https://asuracomic.net/series/solo-leveling/chapter-112");
        Assert.Contains(result.Candidates, c => c.Url == "https://mangadex.org/title/abc/solo-leveling");
        Assert.DoesNotContain(result.Candidates, c => c.Url.Contains("webtoon.xyz"));
        Assert.DoesNotContain(result.Candidates, c => c.Url.Contains("reddit.com"));
        Assert.DoesNotContain(result.Candidates, c => c.Url.Contains("192.168.1.50"));

        var stage = Assert.Single(result.Stages);
        Assert.Equal("SearXNG", stage.Provider);
        Assert.Null(stage.FailureReason);
        Assert.Equal("SearXNG: 2 results", result.Detail);
    }

    [Fact]
    public async Task RestrictToPreferredHost_AppendsSiteOperator()
    {
        HttpRequestMessage? captured = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            return Json(HttpStatusCode.OK, """{ "results": [] }""");
        });

        await CreateService(handler).SearchWithDiagnosticsAsync(
            Extraction, "webtoon.xyz", new SearchRunContext(), default, preferredHost: "asuracomic.net", restrictToPreferredHost: true);

        var query = Uri.UnescapeDataString(captured!.RequestUri!.Query).Replace('+', ' ');
        Assert.Contains("site:asuracomic.net", query);
    }

    [Fact]
    public async Task EmptyBaseUrl_DisablesProvider_WithoutHttpCall()
    {
        var calls = 0;
        var handler = new StubHandler(_ => { calls++; return Json(HttpStatusCode.OK, Fixture()); });

        var result = await CreateService(handler, baseUrl: "").SearchWithDiagnosticsAsync(
            Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.Equal(0, calls);
        Assert.Empty(result.Candidates);
        var stage = Assert.Single(result.Stages);
        Assert.Equal("SearXNG", stage.Provider);
        Assert.Equal("not configured (empty base URL)", stage.FailureReason);
    }

    [Fact]
    public async Task HttpFailure_IsReportedWithSafeDetail_WithoutLeakingBody()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("SECRET-SEARXNG-BODY", Encoding.UTF8, "text/plain")
        });

        var result = await CreateService(handler).SearchWithDiagnosticsAsync(
            Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.Equal("HTTP 500", Assert.Single(result.Stages).FailureReason);
        Assert.Contains("SearXNG: HTTP 500", result.Detail);
        Assert.DoesNotContain("SECRET", result.Detail);
    }

    [Fact]
    public async Task JsonFormatDisabled_403_IsReported()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await CreateService(handler).SearchWithDiagnosticsAsync(
            Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.Equal("JSON format not enabled on the SearXNG instance", Assert.Single(result.Stages).FailureReason);
    }

    [Fact]
    public async Task RepeatedFailures_OpenTheCircuit_ForTheRestOfTheRun()
    {
        var calls = 0;
        var handler = new StubHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.InternalServerError); });
        var service = CreateService(handler);
        var run = new SearchRunContext();

        for (var i = 0; i < SearchRunContext.FailureThreshold; i++)
        {
            await service.SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", run, default);
        }

        var skipped = await service.SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", run, default);

        Assert.Equal(SearchRunContext.FailureThreshold, calls);
        Assert.Equal("HTTP 500 (skipped for this run)", Assert.Single(skipped.Stages).FailureReason);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
