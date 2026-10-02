using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.UrlMigration;
using BookmarkManager.Contracts;
using BookmarkManager.UnitTests.UrlMigration.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests.UrlMigration;

public sealed class TavilySearchServiceTests
{
    private static readonly SeriesExtraction Extraction = new("Solo Leveling", "112", "manhwa", false);

    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UrlMigration", "Fixtures", "tavily-results.json"));

    private static TavilySearchService CreateService(HttpMessageHandler handler, string? apiKey = "tvly-test-key") =>
        new(new SingleClientFactory(new HttpClient(handler)),
            new InMemoryAiTaggingSettingsService(new AiTaggingSettingsDto { TavilyApiKey = apiKey }),
            NullLogger<TavilySearchService>.Instance);

    [Fact]
    public void ParseResults_MapsUrlTitleAndContent_AndSkipsMalformedEntries()
    {
        var parsed = TavilySearchService.ParseResults("""
            {
              "results": [
                { "url": "https://asuracomic.net/series/solo-leveling/chapter-112", "title": "Asura", "content": "reader", "score": 0.9 },
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
        Assert.Empty(TavilySearchService.ParseResults("""{ "query": "x", "response_time": 0.1 }"""));
    }

    [Fact]
    public async Task RequestShape_PostsToConstantEndpoint_WithBearerKeyAndBasicDepth_AndNoKeyInUrl()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, Fixture());
        });

        var result = await CreateService(handler).SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.NotNull(captured);
        Assert.Equal("POST", captured!.Method.Method);
        Assert.Equal("https://api.tavily.com/search", captured.RequestUri!.AbsoluteUri);
        Assert.Equal(string.Empty, captured.RequestUri.Query);

        // Key travels only in the Authorization header.
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("tvly-test-key", captured.Headers.Authorization.Parameter);
        Assert.DoesNotContain("tvly-test-key", captured.RequestUri.AbsoluteUri, StringComparison.Ordinal);

        Assert.NotNull(capturedBody);
        using var body = JsonDocument.Parse(capturedBody!);
        Assert.Equal("Solo Leveling manhwa chapter 112", body.RootElement.GetProperty("query").GetString());
        Assert.Equal("basic", body.RootElement.GetProperty("search_depth").GetString());
        Assert.Equal(10, body.RootElement.GetProperty("max_results").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("include_domains", out _));

        // Dead host, noise host, and the private IP are filtered; the two reader pages survive.
        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Candidates, c => c.Url == "https://asuracomic.net/series/solo-leveling/chapter-112");
        Assert.Contains(result.Candidates, c => c.Url == "https://mangadex.org/title/abc/solo-leveling");
        Assert.DoesNotContain(result.Candidates, c => c.Url.Contains("webtoon.xyz"));
        Assert.DoesNotContain(result.Candidates, c => c.Url.Contains("reddit.com"));
        Assert.DoesNotContain(result.Candidates, c => c.Url.Contains("192.168.1.50"));

        var stage = Assert.Single(result.Stages);
        Assert.Equal("Tavily", stage.Provider);
        Assert.Null(stage.FailureReason);
        Assert.Equal("Tavily: 2 results", result.Detail);
    }

    [Fact]
    public async Task RequestShape_RestrictToPreferredHost_UsesIncludeDomains_NotSiteToken()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var handler = new StubHandler(request =>
        {
            captured = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, """{ "results": [] }""");
        });

        await CreateService(handler).SearchWithDiagnosticsAsync(
            Extraction, "webtoon.xyz", new SearchRunContext(), default, preferredHost: "asuracomic.net", restrictToPreferredHost: true);

        Assert.NotNull(capturedBody);
        using var body = JsonDocument.Parse(capturedBody!);
        var includeDomains = body.RootElement.GetProperty("include_domains");
        Assert.Equal(JsonValueKind.Array, includeDomains.ValueKind);
        Assert.Equal("asuracomic.net", Assert.Single(includeDomains.EnumerateArray()).GetString());
        Assert.DoesNotContain("site:", capturedBody!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnknownOrEmptyMediaType_IsOmittedFromQuery(string mediaType)
    {
        string? capturedBody = null;
        var handler = new StubHandler(request =>
        {
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.OK, """{ "results": [] }""");
        });
        var extraction = new SeriesExtraction("Solo Leveling", "112", mediaType, false);

        await CreateService(handler).SearchWithDiagnosticsAsync(extraction, "webtoon.xyz", new SearchRunContext(), default);

        using var body = JsonDocument.Parse(capturedBody!);
        var query = body.RootElement.GetProperty("query").GetString();
        Assert.Equal("Solo Leveling chapter 112", query);
        Assert.DoesNotContain("unknown", query!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unauthorized_IsReportedWithSafeReason_WithoutLeakingBody()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""{"detail":{"error":"Unauthorized: missing or invalid API key."}}""", Encoding.UTF8, "application/json")
        });

        var result = await CreateService(handler).SearchWithDiagnosticsAsync(
            Extraction, "webtoon.xyz", new SearchRunContext(), default);

        var reason = Assert.Single(result.Stages).FailureReason;
        Assert.Contains("HTTP 401", reason);
        Assert.Contains("invalid or unauthorized key", reason);
        Assert.DoesNotContain("tvly-test-key", result.Detail);
    }

    [Theory]
    [InlineData(432)]
    [InlineData(433)]
    public async Task PlanOrPayGoLimit_IsReportedAsCreditLimit(int status)
    {
        var handler = new StubHandler(_ => new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("""{"detail":{"error":"This request exceeds your plan's set usage limit."}}""", Encoding.UTF8, "application/json")
        });

        var result = await CreateService(handler).SearchWithDiagnosticsAsync(
            Extraction, "webtoon.xyz", new SearchRunContext(), default);

        var reason = Assert.Single(result.Stages).FailureReason;
        Assert.Contains($"HTTP {status}", reason);
        Assert.Contains("credit limit reached", reason);
    }

    [Fact]
    public async Task RateLimit_429_IsReported()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests));

        var result = await CreateService(handler).SearchWithDiagnosticsAsync(
            Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.Equal("rate limited (HTTP 429)", Assert.Single(result.Stages).FailureReason);
    }

    [Fact]
    public async Task RepeatedFailures_OpenTheCircuit_ForTheRestOfTheRun()
    {
        var calls = 0;
        var handler = new StubHandler(_ => { calls++; return new HttpResponseMessage(HttpStatusCode.Unauthorized); });
        var service = CreateService(handler);
        var run = new SearchRunContext();

        for (var i = 0; i < SearchRunContext.FailureThreshold; i++)
        {
            await service.SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", run, default);
        }

        var skipped = await service.SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", run, default);

        Assert.Equal(SearchRunContext.FailureThreshold, calls);
        Assert.Contains("skipped for this run", Assert.Single(skipped.Stages).FailureReason);
    }

    [Fact]
    public async Task MissingKey_IsReported_WithoutHttpCall()
    {
        var calls = 0;
        var handler = new StubHandler(_ => { calls++; return Json(HttpStatusCode.OK, Fixture()); });

        var result = await CreateService(handler, apiKey: "").SearchWithDiagnosticsAsync(
            Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.Equal(0, calls);
        Assert.Equal("API key not configured", Assert.Single(result.Stages).FailureReason);
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
