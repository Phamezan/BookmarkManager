using System.Diagnostics;
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
        // Lowered thinking for this latency-sensitive retrieval call (Gemini 3.x thinkingLevel).
        Assert.True(body.RootElement.TryGetProperty("generationConfig", out var generationConfig));
        Assert.Equal("low", generationConfig.GetProperty("thinkingConfig").GetProperty("thinkingLevel").GetString());

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

    [Fact]
    public void GenerateContentBudget_IsLargerThanTheOldFifteenSecondWrapper()
    {
        // Grounded calls are measured at ~8 s and sometimes exceed 15 s; the old single 15 s budget
        // discarded a successful answer. The call now owns a larger budget separate from redirects.
        Assert.True(GeminiGroundedSearchService.GenerateContentTimeout > TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task SlowGenerateContent_WithinItsOwnBudget_DoesNotTimeOutTheProvider()
    {
        var handler = new DelayedHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                // 160 ms stands in for the >15 s and <30 s production call; budgets are injected
                // small to keep the test fast.
                await Task.Delay(TimeSpan.FromMilliseconds(160), ct);
                return Json(HttpStatusCode.OK, Fixture());
            }

            var token = request.RequestUri!.ToString();
            return RedirectTo(token.Contains("MANGADEX")
                ? "https://mangadex.org/title/abc"
                : token.Contains("DEADHOST")
                    ? "https://webtoon.xyz/read/solo-leveling/chapter-112"
                    : "https://asuracomic.net/series/solo-leveling/chapter-112");
        });

        var service = CreateBudgetedService(handler,
            generateContent: TimeSpan.FromMilliseconds(300),
            redirectResolution: TimeSpan.FromMilliseconds(500),
            redirectRequest: TimeSpan.FromMilliseconds(400),
            provider: TimeSpan.FromSeconds(3));

        var result = await service.SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.Null(Assert.Single(result.Stages).FailureReason);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public async Task HangingRedirect_IsSkipped_OtherRedirectsStillResolve_AndCallSucceeds()
    {
        var handler = new DelayedHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json(HttpStatusCode.OK, ThreeRedirectChunks("HANG_TOKEN", "OK1_TOKEN", "OK2_TOKEN"));
            }

            var token = request.RequestUri!.ToString();
            if (token.Contains("HANG_TOKEN"))
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                return RedirectTo("https://never.example/should-not-arrive");
            }

            return RedirectTo(token.Contains("OK1_TOKEN")
                ? "https://asuracomic.net/series/solo-leveling/chapter-112"
                : "https://mangadex.org/title/abc");
        });

        var service = CreateBudgetedService(handler,
            generateContent: TimeSpan.FromMilliseconds(500),
            redirectResolution: TimeSpan.FromSeconds(2),
            redirectRequest: TimeSpan.FromMilliseconds(200),
            provider: TimeSpan.FromSeconds(4));

        var elapsed = Stopwatch.StartNew();
        var result = await service.SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", new SearchRunContext(), default);
        elapsed.Stop();

        Assert.Null(Assert.Single(result.Stages).FailureReason);
        Assert.Equal(2, result.Candidates.Count);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(1.5), $"hanging redirect was not bounded: {elapsed.Elapsed}");
    }

    [Fact]
    public async Task AllRedirectsFail_TextUrlsFromTheAnswerAreStillReturned()
    {
        var handler = new DelayedHandler((request, _) => request.Method == HttpMethod.Post
            ? Task.FromResult(Json(HttpStatusCode.OK, """
                {
                  "candidates": [{
                    "content": { "parts": [ { "text": "Read at https://asuracomic.net/series/solo-leveling/chapter-112" } ] },
                    "groundingMetadata": { "groundingChunks": [
                      { "web": { "uri": "https://vertexaisearch.cloud.google.com/grounding-api-redirect/FAIL_TOKEN", "title": "asuracomic.net" } }
                    ] }
                  }]
                }
                """))
            : Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));

        var service = CreateBudgetedService(handler,
            generateContent: TimeSpan.FromMilliseconds(500),
            redirectResolution: TimeSpan.FromSeconds(2),
            redirectRequest: TimeSpan.FromMilliseconds(300),
            provider: TimeSpan.FromSeconds(4));

        var result = await service.SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.Null(Assert.Single(result.Stages).FailureReason);
        Assert.Equal("https://asuracomic.net/series/solo-leveling/chapter-112", Assert.Single(result.Candidates).Url);
    }

    [Fact]
    public async Task RedirectsResolveConcurrently()
    {
        var handler = new DelayedHandler(async (request, ct) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return Json(HttpStatusCode.OK, ThreeRedirectChunks("A_TOKEN", "B_TOKEN", "C_TOKEN"));
            }

            await Task.Delay(TimeSpan.FromMilliseconds(150), ct);
            return RedirectTo("https://asuracomic.net/series/solo-leveling/chapter-112");
        });

        var service = CreateBudgetedService(handler,
            generateContent: TimeSpan.FromSeconds(1),
            redirectResolution: TimeSpan.FromSeconds(3),
            redirectRequest: TimeSpan.FromSeconds(2),
            provider: TimeSpan.FromSeconds(5));

        var result = await service.SearchWithDiagnosticsAsync(Extraction, "webtoon.xyz", new SearchRunContext(), default);

        Assert.True(handler.MaxInFlight >= 2, $"redirects were resolved sequentially (max in flight {handler.MaxInFlight})");
        Assert.Null(Assert.Single(result.Stages).FailureReason);
    }

    private static GeminiGroundedSearchService CreateBudgetedService(
        HttpMessageHandler handler,
        TimeSpan? generateContent = null,
        TimeSpan? redirectResolution = null,
        TimeSpan? redirectRequest = null,
        TimeSpan? provider = null) =>
        new(new SingleClientFactory(new HttpClient(handler)),
            new InMemoryAiTaggingSettingsService(GeminiSettings()),
            NullLogger<GeminiGroundedSearchService>.Instance)
        {
            GenerateContentBudget = generateContent ?? GeminiGroundedSearchService.GenerateContentTimeout,
            RedirectResolutionBudget = redirectResolution ?? TimeSpan.FromSeconds(6),
            RedirectRequestBudget = redirectRequest ?? TimeSpan.FromSeconds(5),
            ProviderBudget = provider ?? GeminiGroundedSearchService.ProviderTimeout,
        };

    private static string ThreeRedirectChunks(params string[] tokens)
    {
        var chunks = tokens
            .Select(token => new { web = new { uri = $"https://vertexaisearch.cloud.google.com/grounding-api-redirect/{token}", title = token } })
            .ToArray();
        return JsonSerializer.Serialize(new
        {
            candidates = new[]
            {
                new
                {
                    content = new { parts = new[] { new { text = "no urls" } } },
                    groundingMetadata = new { groundingChunks = chunks }
                }
            }
        });
    }

    private static HttpResponseMessage RedirectTo(string url)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(url);
        return response;
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

    private sealed class DelayedHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;
        private int _inFlight;
        private int _maxInFlight;

        public DelayedHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) => _responder = responder;

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                return await _responder(request, cancellationToken);
            }

            var current = Interlocked.Increment(ref _inFlight);
            int observed;
            while (current > (observed = Volatile.Read(ref _maxInFlight))
                && Interlocked.CompareExchange(ref _maxInFlight, current, observed) != observed)
            {
            }

            try
            {
                return await _responder(request, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
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
