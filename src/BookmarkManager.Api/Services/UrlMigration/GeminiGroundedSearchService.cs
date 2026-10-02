using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// URL Migrator v2 search provider backed by Gemini with Google Search grounding. Sends the
/// series/chapter prompt with the <c>google_search</c> tool, collects grounded web sources plus any
/// http(s) URLs in the model text, resolves Google's grounding redirect links to their final
/// destinations, and runs the result through <see cref="SearchCandidateFilter"/>.
/// </summary>
/// <remarks>
/// Request/response shape verified against https://ai.google.dev/gemini-api/docs/generate-content/google-search
/// (last updated 2026-09-02): POST <c>{base}/models/{model}:generateContent</c>, header
/// <c>x-goog-api-key</c>, body <c>{ "contents": [...], "tools": [ { "google_search": {} } ] }</c>;
/// sources arrive under <c>candidates[0].groundingMetadata.groundingChunks[].web.uri</c> as
/// <c>vertexaisearch.cloud.google.com/grounding-api-redirect/...</c> links.
/// </remarks>
public sealed partial class GeminiGroundedSearchService
{
    /// <summary>Budget for the grounded <c>generateContent</c> call itself. A grounded call on
    /// Gemini 3.8 Flash routinely takes ~8 s and can exceed 15 s, so it must not share a budget
    /// with redirect resolution (which used to be charged against the same 15 s window).</summary>
    public static readonly TimeSpan GenerateContentTimeout = TimeSpan.FromSeconds(30);
    private const int MaxRedirectsToResolve = 6;
    /// <summary>Overall budget for resolving every grounding redirect in parallel. Independent of
    /// the generateContent call; slow redirects are skipped and never fail the whole provider.</summary>
    private static readonly TimeSpan DefaultRedirectResolutionTimeout = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan DefaultRedirectRequestTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Outer per-provider budget (call plus redirects) consumed by the run circuit.</summary>
    public static readonly TimeSpan ProviderTimeout = GenerateContentTimeout + DefaultRedirectResolutionTimeout;

    // Test seams (InternalsVisibleTo): production uses the static defaults above.
    internal TimeSpan GenerateContentBudget { get; init; } = GenerateContentTimeout;
    internal TimeSpan RedirectResolutionBudget { get; init; } = DefaultRedirectResolutionTimeout;
    internal TimeSpan RedirectRequestBudget { get; init; } = DefaultRedirectRequestTimeout;
    internal TimeSpan ProviderBudget { get; init; } = ProviderTimeout;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AiTaggingSettingsService _settings;
    private readonly ILogger<GeminiGroundedSearchService> _logger;

    public GeminiGroundedSearchService(
        IHttpClientFactory httpClientFactory,
        AiTaggingSettingsService settings,
        ILogger<GeminiGroundedSearchService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public const string HttpClientName = nameof(GeminiGroundedSearchService);

    public async Task<SearchOutcome<SearchCandidate>> SearchWithDiagnosticsAsync(
        SeriesExtraction extraction,
        string deadHost,
        SearchRunContext run,
        CancellationToken ct,
        string? preferredHost = null,
        bool restrictToPreferredHost = false)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentException.ThrowIfNullOrWhiteSpace(deadHost);

        var settings = await _settings.GetAsync(ct).ConfigureAwait(false);
        return await run.ExecuteAsync("Gemini", ProviderBudget, async token =>
        {
            var raw = await SearchAsync(extraction, deadHost, preferredHost, restrictToPreferredHost, settings, token).ConfigureAwait(false);
            return SearchCandidateFilter.Filter(raw, deadHost);
        }, _logger, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<SearchCandidate>> SearchAsync(
        SeriesExtraction extraction,
        string deadHost,
        string? preferredHost,
        bool restrictToPreferredHost,
        AiTaggingSettingsDto settings,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(settings.GeminiApiKey))
        {
            throw new SearchResponseException("API key not configured");
        }

        var baseUrl = string.IsNullOrWhiteSpace(settings.Endpoint)
            ? "https://generativelanguage.googleapis.com/v1beta"
            : settings.Endpoint.Trim().TrimEnd('/');
        var model = string.IsNullOrWhiteSpace(settings.GeminiSearchModel) ? "gemini-3.8-flash" : settings.GeminiSearchModel.Trim();
        var uri = new Uri($"{baseUrl}/models/{Uri.EscapeDataString(model)}:generateContent");

        var body = new
        {
            contents = new[] { new { parts = new[] { new { text = BuildPrompt(extraction, deadHost, preferredHost, restrictToPreferredHost) } } } },
            tools = new[] { new { google_search = new { } } },
            // Gemini 3.x uses thinkingLevel (not the 2.5 thinkingBudget); "low" minimises latency
            // for this retrieval call. Docs: https://ai.google.dev/gemini-api/docs/thinking
            generationConfig = new { thinkingConfig = new { thinkingLevel = "low" } }
        };

        var http = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.TryAddWithoutValidation("x-goog-api-key", settings.GeminiApiKey);
        request.Content = JsonContent.Create(body);

        string json;
        using (var callBudget = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            callBudget.CancelAfter(GenerateContentBudget);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, callBudget.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw new HttpRequestException("Gemini rate limit reached.", null, HttpStatusCode.TooManyRequests);
            }

            if (!response.IsSuccessStatusCode)
            {
                // Google returns {"error":{"code","status","message"}}; surface the status token and
                // a sanitized message so a 402/403 is diagnosable without leaking the body. The
                // reason is a hard failure, so a repeated one opens the run circuit like a 429.
                var failureBody = await response.Content.ReadAsStringAsync(callBudget.Token).ConfigureAwait(false);
                var reason = GeminiApiError.Describe(response.StatusCode, failureBody);
                _logger.LogWarning("Gemini search request failed with {Reason}", reason);
                throw new SearchProviderFailureException(reason);
            }

            json = await response.Content.ReadAsStringAsync(callBudget.Token).ConfigureAwait(false);
        }

        var (sources, textUrls) = ParseResponse(json);

        // Redirect resolution is best-effort and parallel under its own budget: a slow or hanging
        // redirect is skipped, never allowed to turn a successful grounded answer into a timeout.
        var redirectsToResolve = sources
            .Where(source => IsGoogleGroundingRedirect(source.Uri))
            .Take(MaxRedirectsToResolve)
            .ToList();

        var resolvedRedirects = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (redirectsToResolve.Count > 0)
        {
            using var redirectBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            redirectBudget.CancelAfter(RedirectResolutionBudget);
            var tasks = redirectsToResolve
                .Select(async source => (Uri: source.Uri!, Final: await ResolveRedirectAsync(http, source.Uri!, ct, redirectBudget.Token).ConfigureAwait(false)))
                .ToArray();
            try
            {
                foreach (var (redirectUri, final) in await Task.WhenAll(tasks).ConfigureAwait(false))
                {
                    resolvedRedirects[redirectUri] = final;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
        }

        var resolved = new List<SearchCandidate>();
        foreach (var source in sources)
        {
            string url;
            if (IsGoogleGroundingRedirect(source.Uri))
            {
                if (!resolvedRedirects.TryGetValue(source.Uri!, out var finalUrl) || finalUrl is null)
                {
                    continue;
                }

                url = finalUrl;
            }
            else
            {
                url = source.Uri!;
            }

            resolved.Add(new SearchCandidate(url, source.Title, "Google Search grounding"));
        }

        resolved.AddRange(textUrls.Select(url => new SearchCandidate(url, null, "Gemini response")));
        return resolved;
    }

    private static string BuildPrompt(SeriesExtraction extraction, string deadHost, string? preferredHost, bool restrictToPreferredHost)
    {
        var chapterText = string.IsNullOrWhiteSpace(extraction.ChapterNumber) ? "an unspecified chapter" : extraction.ChapterNumber;
        // "unknown" is the extraction placeholder for an unclassified bookmark; sending it to the
        // model adds a meaningless token, so omit the parenthetical entirely in that case.
        var mediaTypeText = extraction.HasKnownMediaType ? $" ({extraction.MediaType})" : string.Empty;
        var preferredHostLine = string.IsNullOrWhiteSpace(preferredHost)
            ? string.Empty
            : restrictToPreferredHost
                ? $"Only return links on {preferredHost} - the user already picked it as the migration target.\n"
                : $"Strongly prefer {preferredHost} if it hosts this series.\n";
        return
            $"Find the current official or working reader page to read {extraction.SeriesName}{mediaTypeText} at chapter {chapterText}.\n" +
            $"The site {deadHost} is permanently offline - never return links on it.\n" +
            preferredHostLine +
            "Prefer direct reader pages (the chapter itself), then the series overview page.\n" +
            "Avoid wikis, forums, Reddit, YouTube, social media, news, and store pages.";
    }

    private async Task<string?> ResolveRedirectAsync(HttpClient http, string redirectUrl, CancellationToken runToken, CancellationToken redirectBudgetToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(runToken, redirectBudgetToken);
        timeout.CancelAfter(RedirectRequestBudget);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            // Only real redirects carry a meaningful Location. 201 Created (and 200/401/403) may
            // also include one, but it is not a resolved reader destination.
            if (!IsRedirectStatus(response.StatusCode))
            {
                return null;
            }

            var location = response.Headers.Location;
            if (location is null)
            {
                return null;
            }

            var resolved = location.IsAbsoluteUri
                ? location.ToString()
                : new Uri(new Uri(redirectUrl), location).ToString();
            // A relative Location resolves back onto the Google redirect domain; a still-Google
            // destination is never a reader page.
            return IsGoogleHost(resolved) ? null : resolved;
        }
        catch (OperationCanceledException) when (runToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A redirect that cannot be resolved is skipped, not fatal to the whole search.
            _logger.LogDebug("Gemini grounding redirect resolution failed: {Reason}", ex.GetType().Name);
            return null;
        }
    }

    private static bool IsRedirectStatus(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently      // 301
            or HttpStatusCode.Found                     // 302
            or HttpStatusCode.SeeOther                  // 303
            or HttpStatusCode.TemporaryRedirect         // 307
            or HttpStatusCode.PermanentRedirect;        // 308

    /// <summary>
    /// SSRF guard: only an absolute HTTPS URI on Google's grounding-redirect host may be fetched.
    /// A substring match is not enough - internal/loopback URLs could otherwise be probed.
    /// </summary>
    private static bool IsGoogleGroundingRedirect(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.Equals("vertexaisearch.cloud.google.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>True for google.com and its subdomains (including the vertexaisearch redirect host).</summary>
    private static bool IsGoogleHost(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Host.Equals("google.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase));

    private static (IReadOnlyList<(string? Uri, string? Title)> Sources, IReadOnlyList<string> TextUrls) ParseResponse(string json)
    {
        var sources = new List<(string?, string?)>();
        var textUrls = new List<string>();

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
        {
            return (sources, textUrls);
        }

        var first = candidates.EnumerateArray().FirstOrDefault();
        if (first.ValueKind != JsonValueKind.Object)
        {
            return (sources, textUrls);
        }

        if (first.TryGetProperty("groundingMetadata", out var metadata)
            && metadata.TryGetProperty("groundingChunks", out var chunks)
            && chunks.ValueKind == JsonValueKind.Array)
        {
            foreach (var chunk in chunks.EnumerateArray())
            {
                if (chunk.TryGetProperty("web", out var web) && web.ValueKind == JsonValueKind.Object)
                {
                    var uri = web.TryGetProperty("uri", out var uriElement) ? uriElement.GetString() : null;
                    var title = web.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(uri))
                    {
                        sources.Add((uri, title));
                    }
                }
            }
        }

        if (first.TryGetProperty("content", out var content)
            && content.TryGetProperty("parts", out var parts)
            && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                var text = part.TryGetProperty("text", out var textElement) ? textElement.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                foreach (Match match in HttpUrlRegex().Matches(text))
                {
                    var candidate = match.Value.TrimEnd('.', ',', ')', ']');
                    if (Uri.TryCreate(candidate, UriKind.Absolute, out var parsed)
                        && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
                    {
                        textUrls.Add(candidate);
                    }
                }
            }
        }

        return (sources, textUrls);
    }

    [GeneratedRegex(@"https?://[^\s""'<>\)\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex HttpUrlRegex();
}
