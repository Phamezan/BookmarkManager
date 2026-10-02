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
    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(15);
    private const int MaxRedirectsToResolve = 6;
    private static readonly TimeSpan RedirectTimeout = TimeSpan.FromSeconds(5);

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
        return await run.ExecuteAsync("Gemini", ProviderTimeout, async token =>
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
            tools = new[] { new { google_search = new { } } }
        };

        var http = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        request.Headers.TryAddWithoutValidation("x-goog-api-key", settings.GeminiApiKey);
        request.Content = JsonContent.Create(body);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new HttpRequestException("Gemini rate limit reached.", null, HttpStatusCode.TooManyRequests);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Gemini API request failed with status {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var (sources, textUrls) = ParseResponse(json);

        var resolved = new List<SearchCandidate>();
        var redirectBudget = MaxRedirectsToResolve;
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            string url;
            if (IsGoogleGroundingRedirect(source.Uri))
            {
                if (redirectBudget-- <= 0)
                {
                    continue;
                }

                var finalUrl = await ResolveRedirectAsync(http, source.Uri!, ct).ConfigureAwait(false);
                if (finalUrl is null)
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
        var preferredHostLine = string.IsNullOrWhiteSpace(preferredHost)
            ? string.Empty
            : restrictToPreferredHost
                ? $"Only return links on {preferredHost} - the user already picked it as the migration target.\n"
                : $"Strongly prefer {preferredHost} if it hosts this series.\n";
        return
            $"Find the current official or working reader page to read {extraction.SeriesName} ({extraction.MediaType}) at chapter {chapterText}.\n" +
            $"The site {deadHost} is permanently offline - never return links on it.\n" +
            preferredHostLine +
            "Prefer direct reader pages (the chapter itself), then the series overview page.\n" +
            "Avoid wikis, forums, Reddit, YouTube, social media, news, and store pages.";
    }

    private async Task<string?> ResolveRedirectAsync(HttpClient http, string redirectUrl, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RedirectTimeout);
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
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
