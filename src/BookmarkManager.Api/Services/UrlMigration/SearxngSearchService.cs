using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Fallback candidate source for URL Migrator v2. The primary provider (Gemini grounding or
/// Groq compound) runs first; when it fails or yields nothing, this provider queries the
/// self-hosted SearXNG container and supplies raw web results for the very next stage.
/// </summary>
public interface ISearxngSearchService
{
    Task<SearchOutcome<SearchCandidate>> SearchWithDiagnosticsAsync(
        SeriesExtraction extraction, string deadHost, SearchRunContext run, CancellationToken ct,
        string? preferredHost = null, bool restrictToPreferredHost = false);
}

/// <summary>
/// URL Migrator v2 fallback search provider backed by the self-hosted SearXNG container. Calls
/// the JSON search API (<c>GET {base}/search?q=...&amp;format=json</c>), maps <c>results[].url</c>
/// and <c>results[].title</c> to candidates, and runs them through <see cref="SearchCandidateFilter"/>.
/// Reached internally at <c>http://searxng:8080</c> (config <c>UrlMigration:SearxngBaseUrl</c>).
/// </summary>
/// <remarks>
/// Request/response shape verified against https://docs.searxng.org/dev/search_api.html and the
/// settings keys against https://docs.searxng.org/admin/settings/settings_server.html and
/// https://docs.searxng.org/admin/settings/settings_search.html (docs build 2026.10.2). SearXNG
/// answers with <c>{ "results": [ { "url", "title", "content", "engine", "engines", ... } ] }</c>;
/// requesting <c>format=json</c> when the instance has not enabled it returns HTTP 403.
/// </remarks>
public sealed class SearxngSearchService : ISearxngSearchService
{
    public const string HttpClientName = nameof(SearxngSearchService);

    /// <summary>Outer per-provider budget consumed by the run circuit. SearXNG fans out to
    /// several upstream engines per call, so it gets a little more room than the HTML scrapers
    /// used to, while still failing fast enough to fall through to verification.</summary>
    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(8);

    // Test seam (InternalsVisibleTo): production uses the static default above.
    internal TimeSpan ProviderBudget { get; init; } = ProviderTimeout;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly UrlMigrationOptions _options;
    private readonly ILogger<SearxngSearchService> _logger;

    public SearxngSearchService(
        IHttpClientFactory httpClientFactory,
        IOptions<UrlMigrationOptions> options,
        ILogger<SearxngSearchService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SearchOutcome<SearchCandidate>> SearchWithDiagnosticsAsync(
        SeriesExtraction extraction, string deadHost, SearchRunContext run, CancellationToken ct,
        string? preferredHost = null, bool restrictToPreferredHost = false)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentException.ThrowIfNullOrWhiteSpace(deadHost);
        ArgumentNullException.ThrowIfNull(run);

        var baseUrl = _options.SearxngBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            // Disabled by configuration: contribute nothing and never touch the run circuit so the
            // provider cannot open (or clear) a circuit it is not participating in.
            _logger.LogInformation(
                "Migration search provider {Provider} disabled: {Reason}", "SearXNG", "UrlMigration:SearxngBaseUrl is empty");
            return new([], [new("SearXNG", 0, "not configured (empty base URL)")]);
        }

        return await run.ExecuteAsync("SearXNG", ProviderBudget, async token =>
        {
            var raw = await SearchAsync(extraction, preferredHost, restrictToPreferredHost, baseUrl, token).ConfigureAwait(false);
            return SearchCandidateFilter.Filter(raw, deadHost);
        }, _logger, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SearchCandidate>> SearchAsync(
        SeriesExtraction extraction, string? preferredHost, bool restrictToPreferredHost, string baseUrl, CancellationToken ct)
    {
        var query = BuildQuery(extraction, preferredHost, restrictToPreferredHost);
        var uri = new Uri($"{baseUrl}/search?q={Uri.EscapeDataString(query)}&format=json&categories=general&language=en");

        var http = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            throw new SearchResponseException("JSON format not enabled on the SearXNG instance");
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException("SearXNG rate limit reached.", null, HttpStatusCode.TooManyRequests);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"SearXNG request failed with status {(int)response.StatusCode}.", null, response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseResults(json);
    }

    /// <summary>
    /// Maps a SearXNG JSON search response to candidates, preserving order (SearXNG already
    /// merges and ranks engine results). Malformed/non-object entries and blank URLs are skipped.
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

    private static string BuildQuery(SeriesExtraction extraction, string? preferredHost, bool restrictToPreferredHost)
    {
        var chapter = string.IsNullOrWhiteSpace(extraction.ChapterNumber) ? string.Empty : $" chapter {extraction.ChapterNumber}";
        var query = $"{extraction.SeriesName} {extraction.MediaType}{chapter}".Trim();
        if (restrictToPreferredHost && !string.IsNullOrWhiteSpace(preferredHost))
        {
            query += $" site:{preferredHost}";
        }

        return query;
    }
}
