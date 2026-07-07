using System;
using System.Collections.Generic;
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

/// <summary>
/// End-to-end regression guard for the hand-off contracts between the three URL Migrator v2
/// pipeline services (plan §2/§6.1): a single bookmark's title+url flows through
/// <see cref="ISeriesExtractionService"/> -> <see cref="IAlternativeUrlSearchService"/> ->
/// <see cref="ICandidateVerificationService"/> with everything stubbed at the HTTP boundary
/// (no live network, no real API keys). This does not exercise the orchestrator - that is a
/// separate agent's responsibility - it only proves the three services genuinely compose.
/// </summary>
public sealed class PipelineIntegrationTests
{
    private const string DeadHost = "flamecomics.xyz";
    private const string BookmarkTitle = "Solo Leveling - Chapter 110";
    private const string BookmarkUrl = "https://flamecomics.xyz/solo-leveling/chapter-110";
    private const string NewCandidateUrl = "https://asuracomic.net/series/solo-leveling/chapter-110";

    [Fact]
    public async Task FullPipeline_ExtractSearchVerify_ProducesHighConfidenceMatch()
    {
        // Arrange: one HttpClientFactory routes each named client to a canned stub response,
        // simulating the Groq extraction call, the Groq compound search call, and the final
        // page-fetch verification call - exactly the three HTTP boundaries the real services hit.
        var extractionResponseJson = """
            {
                "choices": [
                    {
                        "message": {
                            "role": "assistant",
                            "content": "[{\"id\": 0, \"series\": \"Solo Leveling\", \"chapter\": \"110\", \"mediaType\": \"manhwa\"}]"
                        }
                    }
                ]
            }
            """;

        var searchResponseJson = $$"""
            {
                "choices": [
                    {
                        "message": {
                            "role": "assistant",
                            "content": "{\"candidates\": [{\"url\": \"{{NewCandidateUrl}}\", \"why\": \"official mirror\"}]}"
                        }
                    }
                ]
            }
            """;

        const string verifyPageHtml =
            "<html><head><title>Solo Leveling Chapter 110 - Asura Comic</title></head><body></body></html>";

        var factory = new RoutingHttpClientFactory(new Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>>
        {
            [nameof(GroqSeriesExtractionService)] = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(extractionResponseJson, Encoding.UTF8, "application/json")
            },
            [nameof(GroqCompoundSearchService)] = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(searchResponseJson, Encoding.UTF8, "application/json")
            },
            [HttpCandidateVerificationService.HttpClientName] = req => req.RequestUri!.ToString() == NewCandidateUrl
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(verifyPageHtml, Encoding.UTF8, "text/html")
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var settingsService = new StubAiTaggingSettingsService();
        var duckDuckGo = new NotCalledDuckDuckGoSearchService();

        ISeriesExtractionService extractionService =
            new GroqSeriesExtractionService(factory, settingsService, NullLogger<GroqSeriesExtractionService>.Instance);
        IAlternativeUrlSearchService searchService =
            new GroqCompoundSearchService(factory, settingsService, duckDuckGo, NullLogger<GroqCompoundSearchService>.Instance);
        ICandidateVerificationService verificationService =
            new HttpCandidateVerificationService(factory, NullLogger<HttpCandidateVerificationService>.Instance);

        // Act: chain all three stages exactly as the (not-yet-built) orchestrator will.
        var extraction = await extractionService.ExtractAsync(BookmarkTitle, BookmarkUrl, category: null, CancellationToken.None);
        var candidates = await searchService.SearchAsync(extraction, DeadHost, CancellationToken.None);

        Assert.NotEmpty(candidates);
        var topCandidate = candidates[0];

        var verification = await verificationService.VerifyAsync(topCandidate, extraction, CancellationToken.None);

        // Assert: the hand-off contracts hold end to end.
        Assert.False(extraction.UsedFallback);
        Assert.Equal("Solo Leveling", extraction.SeriesName);
        Assert.Equal("110", extraction.ChapterNumber);
        Assert.Equal("manhwa", extraction.MediaType);

        Assert.Equal(NewCandidateUrl, topCandidate.Url);

        Assert.True(verification.Reachable);
        Assert.True(verification.SeriesMatched);
        Assert.True(verification.ChapterMatched);
    }

    [Fact]
    public async Task FullPipeline_GroqUnavailable_FallsBackToHeuristicExtractionAndStillVerifies()
    {
        // Extraction: no API key -> SeriesExtractionFallback path (pure regex/normalizer, no HTTP).
        var settingsService = new StubAiTaggingSettingsService(apiKey: "");
        var factory = new RoutingHttpClientFactory(new Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>>
        {
            [HttpCandidateVerificationService.HttpClientName] = req => req.RequestUri!.ToString() == NewCandidateUrl
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<html><head><title>Solo Leveling Chapter 110</title></head></html>",
                        Encoding.UTF8, "text/html")
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        ISeriesExtractionService extractionService =
            new GroqSeriesExtractionService(factory, settingsService, NullLogger<GroqSeriesExtractionService>.Instance);

        var extraction = await extractionService.ExtractAsync(BookmarkTitle, BookmarkUrl, category: null, CancellationToken.None);

        Assert.True(extraction.UsedFallback);
        Assert.Equal("110", extraction.ChapterNumber); // extracted from the URL path per the fallback contract.

        // Search: no API key -> DuckDuckGo-only candidate source (no rerank call), filtered by
        // SearchCandidateFilter to drop the dead host.
        var duckDuckGo = new StubDuckDuckGoSearchService(new[]
        {
            NewCandidateUrl,
            $"https://{DeadHost}/solo-leveling/chapter-110", // must be filtered out
            "https://www.reddit.com/r/manga/comments/1", // noise host, must be filtered out
        });

        IAlternativeUrlSearchService searchService =
            new GroqCompoundSearchService(factory, settingsService, duckDuckGo, NullLogger<GroqCompoundSearchService>.Instance);

        var candidates = await searchService.SearchAsync(extraction, DeadHost, CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal(NewCandidateUrl, candidate.Url);

        ICandidateVerificationService verificationService =
            new HttpCandidateVerificationService(factory, NullLogger<HttpCandidateVerificationService>.Instance);

        var verification = await verificationService.VerifyAsync(candidate, extraction, CancellationToken.None);

        Assert.True(verification.Reachable);
        Assert.True(verification.SeriesMatched);
        Assert.True(verification.ChapterMatched);
    }

    private sealed class RoutingHttpClientFactory : IHttpClientFactory
    {
        private readonly IReadOnlyDictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes;

        public RoutingHttpClientFactory(IReadOnlyDictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> routes)
            => _routes = routes;

        public HttpClient CreateClient(string name)
        {
            if (!_routes.TryGetValue(name, out var responder))
                throw new InvalidOperationException($"No stub route registered for HttpClient '{name}'.");

            return new HttpClient(new StubHandler(responder));
        }

        private sealed class StubHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
            public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(_responder(request));
        }
    }

    private sealed class StubAiTaggingSettingsService : AiTaggingSettingsService
    {
        private readonly string _apiKey;

        public StubAiTaggingSettingsService(string apiKey = "test-key")
            : base(NullLogger<AiTaggingSettingsService>.Instance, "unused-path.json")
            => _apiKey = apiKey;

        public override Task<AiTaggingSettingsDto> GetAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AiTaggingSettingsDto
            {
                GroqApiKey = _apiKey,
                GroqModel = "llama-3.3-70b-versatile",
                GroqBaseUrl = "https://api.groq.com/openai/v1",
                GroqRequestsPerMinute = 6000,
                MigrationSearchModel = "groq/compound-mini",
            });
    }

    private sealed class StubDuckDuckGoSearchService : IDuckDuckGoSearchService
    {
        private readonly IReadOnlyList<string> _candidates;
        public StubDuckDuckGoSearchService(IReadOnlyList<string> candidates) => _candidates = candidates;

        public Task<string?> FindAlternativeUrlAsync(string bookmarkTitle, string? category, string deadDomain, CancellationToken ct)
            => throw new NotSupportedException("Retired scoring path should not be used by the search stage.");

        public string CleanBookmarkTitle(string title) => title;

        public Task<IReadOnlyList<string>> GetSearchCandidatesAsync(string query, string deadDomain, CancellationToken ct)
            => Task.FromResult(_candidates);
    }

    private sealed class NotCalledDuckDuckGoSearchService : IDuckDuckGoSearchService
    {
        public Task<string?> FindAlternativeUrlAsync(string bookmarkTitle, string? category, string deadDomain, CancellationToken ct)
            => throw new InvalidOperationException("DuckDuckGo fallback should not be used when Groq compound search succeeds.");

        public string CleanBookmarkTitle(string title) => title;

        public Task<IReadOnlyList<string>> GetSearchCandidatesAsync(string query, string deadDomain, CancellationToken ct)
            => throw new InvalidOperationException("DuckDuckGo fallback should not be used when Groq compound search succeeds.");
    }
}
