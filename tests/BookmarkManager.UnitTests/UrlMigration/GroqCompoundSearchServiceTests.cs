using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.UrlMigration;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BookmarkManager.UnitTests.UrlMigration;

public class GroqCompoundSearchServiceTests
{
    private static readonly SeriesExtraction Extraction = new("Solo Leveling", "112", "manhwa", false);

    [Fact]
    public void ParseCandidatesJson_ParsesPlainJson()
    {
        var content = "{\"candidates\": [{\"url\": \"https://asuracomic.net/series/solo-leveling/chapter-112\", \"why\": \"official mirror\"}]}";

        var result = GroqCompoundSearchService.ParseCandidatesJson(content);

        var candidate = Assert.Single(result);
        Assert.Equal("https://asuracomic.net/series/solo-leveling/chapter-112", candidate.Url);
        Assert.Equal("official mirror", candidate.Snippet);
    }

    [Fact]
    public void ParseCandidatesJson_StripsMarkdownCodeFenceAndSurroundingProse()
    {
        var content = "Sure, here you go:\n```json\n{\"candidates\": [{\"url\": \"https://mangadex.org/title/abc\"}]}\n```\nHope that helps!";

        var result = GroqCompoundSearchService.ParseCandidatesJson(content);

        var candidate = Assert.Single(result);
        Assert.Equal("https://mangadex.org/title/abc", candidate.Url);
    }

    [Fact]
    public void ParseCandidatesJson_UsesFirstCompleteObjectWhenMultiplePresent()
    {
        var content =
            "{\"candidates\": [{\"url\": \"https://asuracomic.net/series/solo-leveling/chapter-112\", \"why\": \"best\"}]}\n" +
            "Also consider:\n" +
            "{\"candidates\": [{\"url\": \"https://mangadex.org/title/other\", \"why\": \"alt\"}]}";

        var result = GroqCompoundSearchService.ParseCandidatesJson(content);

        var candidate = Assert.Single(result);
        Assert.Equal("https://asuracomic.net/series/solo-leveling/chapter-112", candidate.Url);
    }

    [Fact]
    public void ParseCandidatesJson_ReturnsEmptyOnMalformedJson()
    {
        var content = "not json at all, sorry";

        var result = GroqCompoundSearchService.ParseCandidatesJson(content);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseCandidatesJson_ReturnsEmptyWhenCandidatesMissing()
    {
        var content = "{\"answer\": \"no results found\"}";

        var result = GroqCompoundSearchService.ParseCandidatesJson(content);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_FallsBackToSearxng_WhenCompoundCallFails()
    {
        var compoundCallCount = 0;

        var handler = new StubHttpMessageHandler(_ =>
        {
            compoundCallCount++;
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("compound model unavailable", Encoding.UTF8, "text/plain")
            };
        });

        var searxng = new StubSearxngSearchService(new[]
        {
            "https://asuracomic.net/series/solo-leveling/chapter-112",
            "https://www.reddit.com/r/manga/thread",
        });

        var service = new GroqCompoundSearchService(
            new StubHttpClientFactory(new HttpClient(handler)), new StubAiTaggingSettingsService(), searxng, NullLogger<GroqCompoundSearchService>.Instance);

        var result = await service.SearchAsync(Extraction, "flamecomics.xyz", CancellationToken.None);

        Assert.Equal(1, compoundCallCount);
        Assert.True(searxng.WasCalled);
        var candidate = Assert.Single(result);
        Assert.Equal("https://asuracomic.net/series/solo-leveling/chapter-112", candidate.Url);
    }

    [Fact]
    public async Task SearchAsync_WhenCompoundSucceeds_SearxngIsNotCalled()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CompoundJson("https://asuracomic.net/series/solo-leveling/chapter-112"), Encoding.UTF8, "application/json")
        });
        var searxng = new StubSearxngSearchService(new[] { "https://mangadex.org/title/abc" });
        var service = new GroqCompoundSearchService(
            new StubHttpClientFactory(new HttpClient(handler)), new StubAiTaggingSettingsService(), searxng, NullLogger<GroqCompoundSearchService>.Instance);

        var result = await service.SearchAsync(Extraction, "flamecomics.xyz", CancellationToken.None);

        Assert.False(searxng.WasCalled);
        Assert.Equal("https://asuracomic.net/series/solo-leveling/chapter-112", Assert.Single(result).Url);
    }

    [Fact]
    public async Task FilteredCompoundResults_FallBackToSearxng()
    {
        var handler = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(CompoundJson("https://reddit.com/r/manga"), Encoding.UTF8, "application/json")
        });
        var searxng = new StubSearxngSearchService(["https://asuracomic.net/series/academy/chapter-51"]);
        var service = new GroqCompoundSearchService(
            new StubHttpClientFactory(new HttpClient(handler)), new StubAiTaggingSettingsService(), searxng, NullLogger<GroqCompoundSearchService>.Instance);

        var result = await service.SearchAsync(Extraction, "www.webtoon.xyz", default);

        Assert.True(searxng.WasCalled);
        Assert.Equal("https://asuracomic.net/series/academy/chapter-51", Assert.Single(result).Url);
    }

    [Fact]
    public async Task RetiredPublicCompoundModel_IsSkippedWithoutHttpCall_ThenFallsBackToSearxng()
    {
        var calls = 0;
        var handler = new StubHttpMessageHandler(_ => { calls++; throw new InvalidOperationException("Should not call retired model"); });
        var settings = new TestDoubles.InMemoryAiTaggingSettingsService(new AiTaggingSettingsDto
        {
            GroqApiKey = "test", GroqBaseUrl = "https://api.groq.com/openai/v1", MigrationSearchModel = "groq/compound-mini"
        });
        var searxng = new StubSearxngSearchService(["https://asuracomic.net/series/solo-leveling/chapter-112"]);
        var service = new GroqCompoundSearchService(new StubHttpClientFactory(new HttpClient(handler)), settings, searxng, NullLogger<GroqCompoundSearchService>.Instance);

        var result = await service.SearchWithDiagnosticsAsync(Extraction, "www.webtoon.xyz", new(), default);

        Assert.Equal(0, calls);
        Assert.True(searxng.WasCalled);
        Assert.Contains("model decommissioned", result.Detail);
        Assert.Equal("https://asuracomic.net/series/solo-leveling/chapter-112", Assert.Single(result.Candidates).Url);
    }

    [Fact]
    public async Task RateLimit_IsReported_AndRecordedInSharedThrottle()
    {
        var throttle = new AiRequestThrottle();
        var handler = new StubHttpMessageHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(1));
            return response;
        });
        var service = new GroqCompoundSearchService(
            new StubHttpClientFactory(new HttpClient(handler)), new StubAiTaggingSettingsService(), new StubSearxngSearchService([]), NullLogger<GroqCompoundSearchService>.Instance, throttle);

        var result = await service.SearchWithDiagnosticsAsync(Extraction, "www.webtoon.xyz", new(), default);

        Assert.Contains("Groq compound: rate limited (HTTP 429)", result.Detail);
        // The recorded Retry-After must keep the next caller waiting; an already-cancelled token
        // proves the wait is cancellable without depending on a wall-clock sleep.
        using var timeout = new CancellationTokenSource();
        timeout.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => throttle.AwaitThrottleAsync(1000, timeout.Token));
    }

    [Fact]
    public async Task MigrationSearch_NeverCallsDuckDuckGoOrYahoo()
    {
        // The DDG/Yahoo HTML scrapers are removed from the migration chain (production evidence:
        // bot challenge + HTTP 500). This guards against silently reintroducing them: any call
        // through the named DuckDuckGoTriage/YahooTriage clients would be recorded here.
        var factory = new TrackingHttpClientFactory(name => name == nameof(GroqCompoundSearchService)
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
            : throw new InvalidOperationException($"Unexpected HTTP client '{name}'"));
        var searxng = new StubSearxngSearchService(["https://asuracomic.net/series/solo-leveling/chapter-112"]);
        var service = new GroqCompoundSearchService(factory, new StubAiTaggingSettingsService(), searxng, NullLogger<GroqCompoundSearchService>.Instance);

        var result = await service.SearchAsync(Extraction, "webtoon.xyz", CancellationToken.None);

        Assert.Equal("https://asuracomic.net/series/solo-leveling/chapter-112", Assert.Single(result).Url);
        Assert.DoesNotContain("DuckDuckGoTriage", factory.CreatedClientNames);
        Assert.DoesNotContain("YahooTriage", factory.CreatedClientNames);
    }

    private static string CompoundJson(string url) =>
        System.Text.Json.JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = $"{{\"candidates\":[{{\"url\":\"{url}\",\"why\":\"match\"}}]}}" } } } });

    private sealed class StubAiTaggingSettingsService : AiTaggingSettingsService
    {
        public StubAiTaggingSettingsService() : base(NullLogger<AiTaggingSettingsService>.Instance, "unused-path.json")
        {
        }

        public override Task<AiTaggingSettingsDto> GetAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AiTaggingSettingsDto
            {
                GroqApiKey = "test-key",
                GroqModel = "llama-3.3-70b-versatile",
                GroqBaseUrl = "https://groq-compatible.example/openai/v1",
                GroqRequestsPerMinute = 1000,
                MigrationSearchModel = "groq/compound-mini",
            });
    }

    private sealed class StubSearxngSearchService : ISearxngSearchService
    {
        private readonly IReadOnlyList<SearchCandidate> _candidates;
        public bool WasCalled { get; private set; }

        public StubSearxngSearchService(IReadOnlyList<string> urls)
            => _candidates = urls.Select(url => new SearchCandidate(url, null, null)).ToArray();

        public Task<SearchOutcome<SearchCandidate>> SearchWithDiagnosticsAsync(
            SeriesExtraction extraction, string deadHost, SearchRunContext run, CancellationToken ct,
            string? preferredHost = null, bool restrictToPreferredHost = false)
        {
            WasCalled = true;
            return Task.FromResult(new SearchOutcome<SearchCandidate>(_candidates, [new("SearXNG", _candidates.Count, null)]));
        }
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;
        public StubHttpClientFactory(HttpClient client) => _client = client;
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class TrackingHttpClientFactory : IHttpClientFactory
    {
        private readonly Func<string, HttpResponseMessage> _responder;
        public List<string> CreatedClientNames { get; } = [];

        public TrackingHttpClientFactory(Func<string, HttpResponseMessage> responder) => _responder = responder;

        public HttpClient CreateClient(string name)
        {
            CreatedClientNames.Add(name);
            return new HttpClient(new StubHttpMessageHandler(_ => _responder(name)));
        }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }
}
