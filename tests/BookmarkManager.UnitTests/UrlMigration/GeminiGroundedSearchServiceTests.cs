using System.Net;
using System.Text;
using System.Text.Json;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.UrlMigration;
using BookmarkManager.Contracts;
using BookmarkManager.UnitTests.UrlMigration.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace BookmarkManager.UnitTests.UrlMigration;

public sealed class GeminiGroundedSearchServiceTests
{
    private static readonly SeriesExtraction Extraction = new("Solo Leveling", "112", "manhwa", false);

    private static string Fixture() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "UrlMigration", "Fixtures", "gemini-grounded.json"));

    private static AiTaggingSettingsDto GeminiSettings(string provider = "Gemini") => new()
    {
        MigrationSearchProvider = provider,
        GeminiApiKey = "test-key",
        GeminiSearchModel = "gemini-3.8-flash",
        Endpoint = "https://generativelanguage.googleapis.com/v1beta",
        GroqApiKey = "groq-key",
        GroqBaseUrl = "https://groq-compatible.example/openai/v1",
        GroqModel = "llama-3.3-70b-versatile",
        GroqRequestsPerMinute = 6000,
        MigrationSearchModel = "groq/compound-mini",
    };

    [Fact]
    public async Task RequestShape_ResolvesGroundingRedirects_AndFiltersDeadHostNoise()
    {
        var generateCalls = 0;
        HttpRequestMessage? captured = null;
        string? capturedBody = null;

        var handler = new RoutingHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                generateCalls++;
                captured = request;
                capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json(HttpStatusCode.OK, Fixture());
            }

            var url = request.RequestUri!.ToString();
            var final = url.Contains("ASURA_TOKEN") ? "https://asuracomic.net/series/solo-leveling/chapter-112"
                : url.Contains("MANGADEX_TOKEN") ? "https://mangadex.org/title/abc"
                : url.Contains("DEADHOST_TOKEN") ? "https://webtoon.xyz/read/solo-leveling/chapter-112"
                : null;
            if (final is null)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri(final);
            return redirect;
        });

        var service = new GeminiGroundedSearchService(
            new SingleClientFactory(new HttpClient(handler)),
            new InMemoryAiTaggingSettingsService(GeminiSettings()),
            NullLogger<GeminiGroundedSearchService>.Instance);

        var result = await service.SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.Equal(1, generateCalls);
        Assert.NotNull(captured);
        Assert.Equal("POST", captured!.Method.Method);
        Assert.Contains("models/gemini-3.8-flash:generateContent", captured.RequestUri!.ToString());
        Assert.DoesNotContain("key=", captured.RequestUri!.Query);
        Assert.Equal("test-key", Assert.Single(captured.Headers.GetValues("x-goog-api-key")));

        using var body = JsonDocument.Parse(capturedBody!);
        Assert.True(body.RootElement.TryGetProperty("tools", out var tools));
        Assert.True(tools[0].TryGetProperty("google_search", out _));

        Assert.Equal(2, result.Candidates.Count);
        Assert.Contains(result.Candidates, c => c.Url == "https://asuracomic.net/series/solo-leveling/chapter-112");
        Assert.Contains(result.Candidates, c => c.Url == "https://mangadex.org/title/abc");
        Assert.DoesNotContain(result.Candidates, c => c.Url.Contains("webtoon.xyz"));
        Assert.DoesNotContain(result.Candidates, c => c.Url.Contains("reddit.com"));
    }

    [Fact]
    public async Task RateLimited_FallsBackToHtmlChain_WithoutLeakingResponseBody()
    {
        var geminiCalls = 0;
        var handler = new RoutingHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                geminiCalls++;
                return new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("SECRET-429-BODY", Encoding.UTF8, "text/plain")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        });

        var ddg = new StubDuckDuckGo(["https://asuracomic.net/series/solo-leveling/chapter-112"]);
        var gemini = new GeminiGroundedSearchService(
            new SingleClientFactory(new HttpClient(handler)),
            new InMemoryAiTaggingSettingsService(GeminiSettings()),
            NullLogger<GeminiGroundedSearchService>.Instance);
        var fallbackSettings = GeminiSettings();
        fallbackSettings.GroqApiKey = string.Empty; // no Groq key -> return raw HTML candidates
        var service = new GroqCompoundSearchService(
            new SingleClientFactory(new HttpClient(handler)),
            new InMemoryAiTaggingSettingsService(fallbackSettings),
            ddg,
            NullLogger<GroqCompoundSearchService>.Instance,
            gemini: gemini);

        var result = await service.SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.Equal(1, geminiCalls);
        Assert.True(ddg.WasCalled);
        Assert.Contains("Gemini: rate limited (HTTP 429)", result.Detail);
        Assert.DoesNotContain("SECRET", result.Detail);
        Assert.Equal("https://asuracomic.net/series/solo-leveling/chapter-112", Assert.Single(result.Candidates).Url);
    }

    [Fact]
    public async Task GroqProvider_DoesNotCallGemini()
    {
        var geminiCalls = 0;
        var handler = new RoutingHandler(request =>
        {
            if (request.RequestUri!.Host.Contains("generativelanguage", StringComparison.OrdinalIgnoreCase))
            {
                geminiCalls++;
                return Json(HttpStatusCode.OK, Fixture());
            }

            var content = "{\"candidates\":[{\"url\":\"https://asuracomic.net/series/solo-leveling/chapter-112\"}]}";
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }));
        });

        var gemini = new GeminiGroundedSearchService(
            new SingleClientFactory(new HttpClient(handler)),
            new InMemoryAiTaggingSettingsService(GeminiSettings()),
            NullLogger<GeminiGroundedSearchService>.Instance);
        var service = new GroqCompoundSearchService(
            new SingleClientFactory(new HttpClient(handler)),
            new InMemoryAiTaggingSettingsService(GeminiSettings(provider: "Groq")),
            new StubDuckDuckGo([]),
            NullLogger<GroqCompoundSearchService>.Instance,
            gemini: gemini);

        var result = await service.SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.Equal(0, geminiCalls);
        Assert.Equal("https://asuracomic.net/series/solo-leveling/chapter-112", Assert.Single(result.Candidates).Url);
    }

    [Fact]
    public async Task InternalHostUrls_ContainingRedirectMarker_AreNeverFetched()
    {
        var requestedHosts = new List<string>();
        var handler = new RoutingHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json(HttpStatusCode.OK, """
                    {
                      "candidates": [{
                        "content": { "parts": [ { "text": "no urls in text" } ] },
                        "groundingMetadata": { "groundingChunks": [
                          { "web": { "uri": "http://127.0.0.1:8080/api/settings?grounding-api-redirect=1", "title": "loopback" } },
                          { "web": { "uri": "http://192.168.1.100:8080/api/bookmarks/export?grounding-api-redirect=1", "title": "lan" } },
                          { "web": { "uri": "http://169.254.169.254/latest/meta-data/?grounding-api-redirect=1", "title": "metadata" } },
                          { "web": { "uri": "https://vertexaisearch.cloud.google.com/grounding-api-redirect/OK", "title": "asuracomic.net" } }
                        ] }
                      }]
                    }
                    """);
            }

            requestedHosts.Add(request.RequestUri!.Host);
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri("https://asuracomic.net/series/solo-leveling/chapter-112");
            return redirect;
        });

        var result = await CreateService(handler).SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.DoesNotContain("127.0.0.1", requestedHosts);
        Assert.DoesNotContain("192.168.1.100", requestedHosts);
        Assert.DoesNotContain("169.254.169.254", requestedHosts);
        Assert.Equal(["vertexaisearch.cloud.google.com"], requestedHosts);
        Assert.Equal("https://asuracomic.net/series/solo-leveling/chapter-112", Assert.Single(result.Candidates).Url);
    }

    [Fact]
    public async Task NonRedirectStatusWithLocation_IsIgnored()
    {
        var result = await RunWithSingleChunkAsync("https://vertexaisearch.cloud.google.com/grounding-api-redirect/OK", _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Created);
            response.Headers.Location = new Uri("https://asuracomic.net/created");
            return response;
        });

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task RelativeLocation_ResolvingBackToGoogle_IsRejected()
    {
        var result = await RunWithSingleChunkAsync("https://vertexaisearch.cloud.google.com/grounding-api-redirect/OK", _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("/grounding-api-redirect/relative", UriKind.Relative);
            return response;
        });

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task RedirectNetworkFailure_IsSkippedWithoutThrowing()
    {
        var result = await RunWithSingleChunkAsync("https://vertexaisearch.cloud.google.com/grounding-api-redirect/OK",
            _ => throw new HttpRequestException("connection refused"));

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task EmptyCandidates_ReturnEmpty()
    {
        var handler = new RoutingHandler(_ => Json(HttpStatusCode.OK, """{ "candidates": [] }"""));
        var result = await CreateService(handler).SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task MissingGroundingMetadata_StillExtractsTextUrls()
    {
        var handler = new RoutingHandler(_ => Json(HttpStatusCode.OK, """
            {
              "candidates": [{
                "content": { "parts": [ { "text": "Read it at https://asuracomic.net/series/solo-leveling/chapter-112 please." } ] }
              }]
            }
            """));
        var result = await CreateService(handler).SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.Equal("https://asuracomic.net/series/solo-leveling/chapter-112", Assert.Single(result.Candidates).Url);
    }

    private static GeminiGroundedSearchService CreateService(HttpMessageHandler handler) =>
        new(new SingleClientFactory(new HttpClient(handler)),
            new InMemoryAiTaggingSettingsService(GeminiSettings()),
            NullLogger<GeminiGroundedSearchService>.Instance);

    private static async Task<SearchOutcome<SearchCandidate>> RunWithSingleChunkAsync(
        string chunkUri, Func<HttpRequestMessage, HttpResponseMessage> redirectResponder)
    {
        var handler = new RoutingHandler(request => request.Method == HttpMethod.Post
            ? Json(HttpStatusCode.OK, $$"""
                {
                  "candidates": [{
                    "content": { "parts": [ { "text": "no urls" } ] },
                    "groundingMetadata": { "groundingChunks": [ { "web": { "uri": "{{chunkUri}}", "title": "x" } } ] }
                  }]
                }
                """)
            : redirectResponder(request));

        return await CreateService(handler).SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", new SearchRunContext(), default);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public RoutingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private sealed class StubDuckDuckGo : IDuckDuckGoSearchService
    {
        private readonly IReadOnlyList<string> _candidates;
        public bool WasCalled { get; private set; }
        public StubDuckDuckGo(IReadOnlyList<string> candidates) => _candidates = candidates;

        public Task<SearchOutcome<string>> SearchWithDiagnosticsAsync(string query, string deadDomain, SearchRunContext run, CancellationToken ct)
        {
            WasCalled = true;
            return Task.FromResult(new SearchOutcome<string>(_candidates, [new("DuckDuckGo", _candidates.Count, null)]));
        }
    }
}
