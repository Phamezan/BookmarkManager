using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Primary URL Migrator search provider backed by the Tavily Search API. This is a plain search
/// API (no LLM/grounding billing dependency): the series name + chapter are already extracted and
/// every candidate is verified by <see cref="ICandidateVerificationService"/>, so a search API is
/// cheaper and more reliable than an LLM. Reached via <c>POST https://api.tavily.com/search</c>
/// with <c>search_depth: "basic"</c> (1 free-tier credit per call).
/// </summary>
/// <remarks>
/// Request/response shape and error codes verified against
/// https://docs.tavily.com/documentation/api-reference/endpoint/search and
/// https://docs.tavily.com/documentation/api-credits (fetched 2026-10-02):
/// <list type="bullet">
/// <item>Auth is <c>Authorization: Bearer &lt;key&gt;</c> (the documented <c>bearerAuth</c> scheme);
/// the key is never placed in the URL or query string.</item>
/// <item>Body: <c>query</c> (required), <c>search_depth</c> (<c>basic</c> = 1 credit, <c>advanced</c>
/// = 2), <c>max_results</c> (default 10, max 20), and <c>include_domains</c> (array; defaults to a
/// hard <c>restrict</c> filter via <c>include_domains_mode</c>).</item>
/// <item>Response: <c>results[]</c> with <c>url</c>/<c>title</c>/<c>content</c>/<c>score</c>.</item>
/// <item>Errors: 401 missing/invalid key, 429 rate limit (<c>Retry-After</c>), 432 plan/usage limit,
/// 433 pay-as-you-go limit.</item>
/// </list>
/// </remarks>
public sealed class TavilySearchService : ITavilySearchService
{
    public const string HttpClientName = nameof(TavilySearchService);

    /// <summary>Fixed, non-configurable API origin. No user-editable URL means no endpoint-change
    /// pairing for <c>TavilyApiKey</c> and no way for a caller to re-point the stored key.</summary>
    public const string ApiBaseUrl = "https://api.tavily.com";

    public const string ApiEndpoint = ApiBaseUrl + "/search";

    /// <summary>Outer per-provider budget consumed by the run circuit. Tavily's basic search is a
    /// single fast request, so this fails fast enough to fall through to SearXNG + verification.</summary>
    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(10);

    private const int MaxResults = 10;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AiTaggingSettingsService _settings;
    private readonly ILogger<TavilySearchService> _logger;

    public TavilySearchService(
        IHttpClientFactory httpClientFactory,
        AiTaggingSettingsService settings,
        ILogger<TavilySearchService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SearchOutcome<SearchCandidate>> SearchWithDiagnosticsAsync(
        SeriesExtraction extraction, string deadHost, SearchRunContext run, CancellationToken ct,
        string? preferredHost = null, bool restrictToPreferredHost = false, string? queryOverride = null)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentException.ThrowIfNullOrWhiteSpace(deadHost);
        ArgumentNullException.ThrowIfNull(run);

        var settings = await _settings.GetAsync(ct).ConfigureAwait(false);
        return await run.ExecuteAsync("Tavily", ProviderTimeout, async token =>
        {
            var raw = await SearchAsync(extraction, preferredHost, restrictToPreferredHost, settings, token, queryOverride).ConfigureAwait(false);
            return SearchCandidateFilter.Filter(raw, deadHost);
        }, _logger, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SearchCandidate>> SearchAsync(
        SeriesExtraction extraction,
        string? preferredHost,
        bool restrictToPreferredHost,
        AiTaggingSettingsDto settings,
        CancellationToken ct,
        string? queryOverride = null)
    {
        if (string.IsNullOrWhiteSpace(settings.TavilyApiKey))
        {
            throw new SearchResponseException("API key not configured");
        }

        var restrict = restrictToPreferredHost && !string.IsNullOrWhiteSpace(preferredHost);
        var requestBody = new TavilySearchRequest(
            Query: string.IsNullOrWhiteSpace(queryOverride) ? BuildQuery(extraction) : queryOverride.Trim(),
            SearchDepth: "basic",
            MaxResults: MaxResults,
            IncludeDomains: restrict ? new[] { preferredHost! } : null);

        var http = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiEndpoint);
        // Key travels only in the Authorization header; never the URL/query string or logs.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.TavilyApiKey);
        request.Content = JsonContent.Create(requestBody, options: JsonOptions);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException("Tavily rate limit reached.", null, HttpStatusCode.TooManyRequests);
        }

        if (!response.IsSuccessStatusCode)
        {
            // Surface a sanitized reason (status + optional capped Tavily message); the body and
            // key are never copied. Repeated hard failures open the run circuit like a 429.
            var failureBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var reason = TavilyApiError.Describe(response.StatusCode, failureBody);
            _logger.LogWarning("Tavily search request failed with {Reason}", reason);
            throw new SearchProviderFailureException(reason);
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseResults(json);
    }

    /// <summary>
    /// Maps a Tavily JSON search response to candidates, preserving Tavily's relevance order
    /// (<c>results[]</c> is already sorted by score). Malformed/non-object entries and blank URLs
    /// are skipped.
    /// </summary>
    internal static IReadOnlyList<SearchCandidate> ParseResults(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var candidates = new List<SearchCandidate>();
        foreach (var result in results.EnumerateArray())
        {
            if (result.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var url = result.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var title = result.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
            var snippet = result.TryGetProperty("content", out var contentElement) ? contentElement.GetString() : null;
            candidates.Add(new SearchCandidate(url, title, snippet));
        }

        return candidates;
    }

    private static string BuildQuery(SeriesExtraction extraction)
    {
        var chapter = string.IsNullOrWhiteSpace(extraction.ChapterNumber) ? string.Empty : $" chapter {extraction.ChapterNumber}";
        // "unknown" is the extraction placeholder for an unclassified bookmark; sending it as a
        // literal query token wastes a match term, so omit the media type entirely in that case.
        var mediaType = extraction.HasKnownMediaType ? $" {extraction.MediaType}" : string.Empty;
        return $"{extraction.SeriesName}{mediaType}{chapter}".Trim();
    }

    private sealed record TavilySearchRequest(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("search_depth")] string SearchDepth,
        [property: JsonPropertyName("max_results")] int MaxResults,
        [property: JsonPropertyName("include_domains")] IReadOnlyList<string>? IncludeDomains);
}
