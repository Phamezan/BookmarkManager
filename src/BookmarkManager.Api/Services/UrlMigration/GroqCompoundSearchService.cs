using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Search + rerank stage of URL Migrator v2 (plan §6.3). Primary path is a single Groq
/// "compound" model call per bookmark (live web search + answer with sources, collapsing
/// search and rerank into one round trip). Falls back to DuckDuckGo HTML search as a raw
/// candidate source plus a plain Groq chat rerank call when the compound model errors or is
/// unavailable (e.g. missing API key).
/// </summary>
public sealed class GroqCompoundSearchService : IAlternativeUrlSearchService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AiTaggingSettingsService _settings;
    private readonly IDuckDuckGoSearchService _duckDuckGo;
    private readonly ILogger<GroqCompoundSearchService> _logger;

    // Static: pace every Groq call (compound or plain chat) from this service against the same
    // account-wide rate limit, regardless of how many scoped instances are created per run.
    private static readonly AiRequestThrottle Throttle = new();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public GroqCompoundSearchService(
        IHttpClientFactory httpClientFactory,
        AiTaggingSettingsService settings,
        IDuckDuckGoSearchService duckDuckGo,
        ILogger<GroqCompoundSearchService> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _duckDuckGo = duckDuckGo ?? throw new ArgumentNullException(nameof(duckDuckGo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<SearchCandidate>> SearchAsync(SeriesExtraction extraction, string deadHost, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        if (string.IsNullOrWhiteSpace(deadHost))
        {
            throw new ArgumentException("Dead host is required.", nameof(deadHost));
        }

        var settings = await _settings.GetAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(settings.GroqApiKey))
        {
            try
            {
                var compoundCandidates = await SearchWithCompoundAsync(extraction, deadHost, settings, ct).ConfigureAwait(false);
                if (compoundCandidates.Count > 0)
                {
                    return SearchCandidateFilter.Filter(compoundCandidates, deadHost);
                }

                _logger.LogInformation(
                    "Groq compound search returned no usable candidates for series '{Series}'; falling back to DuckDuckGo + rerank.",
                    extraction.SeriesName);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Groq compound search failed for series '{Series}'; falling back to DuckDuckGo + rerank.",
                    extraction.SeriesName);
            }
        }
        else
        {
            _logger.LogInformation("Groq API key not configured; using DuckDuckGo + rerank fallback for search stage.");
        }

        var fallbackCandidates = await SearchWithFallbackAsync(extraction, deadHost, settings, ct).ConfigureAwait(false);
        return SearchCandidateFilter.Filter(fallbackCandidates, deadHost);
    }

    private async Task<IReadOnlyList<SearchCandidate>> SearchWithCompoundAsync(
        SeriesExtraction extraction,
        string deadHost,
        BookmarkManager.Contracts.AiTaggingSettingsDto settings,
        CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(settings.MigrationSearchModel) ? "groq/compound-mini" : settings.MigrationSearchModel;
        var prompt = BuildSearchPrompt(extraction, deadHost);

        var content = await CallGroqChatAsync(model, SystemPromptForCompound, prompt, settings, ct).ConfigureAwait(false);
        return ParseCandidatesJson(content);
    }

    private async Task<IReadOnlyList<SearchCandidate>> SearchWithFallbackAsync(
        SeriesExtraction extraction,
        string deadHost,
        BookmarkManager.Contracts.AiTaggingSettingsDto settings,
        CancellationToken ct)
    {
        var query = BuildDuckDuckGoQuery(extraction);

        IReadOnlyList<string> rawCandidateUrls;
        try
        {
            rawCandidateUrls = await _duckDuckGo.GetSearchCandidatesAsync(query, deadHost, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DuckDuckGo fallback search failed for query '{Query}'.", query);
            return [];
        }

        if (rawCandidateUrls.Count == 0)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(settings.GroqApiKey))
        {
            // No rerank possible without a key - return the raw (unranked) DDG candidates so the
            // pipeline still has something to verify, rather than nothing at all.
            return rawCandidateUrls.Select(url => new SearchCandidate(url, null, null)).ToList();
        }

        try
        {
            var model = string.IsNullOrWhiteSpace(settings.GroqModel) ? "llama-3.3-70b-versatile" : settings.GroqModel;
            var prompt = BuildRerankPrompt(extraction, deadHost, rawCandidateUrls);
            var content = await CallGroqChatAsync(model, SystemPromptForRerank, prompt, settings, ct).ConfigureAwait(false);
            var reranked = ParseCandidatesJson(content);
            if (reranked.Count > 0)
            {
                return reranked;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Plain Groq rerank fallback failed; returning unranked DuckDuckGo candidates.");
        }

        return rawCandidateUrls.Select(url => new SearchCandidate(url, null, null)).ToList();
    }

    private async Task<string> CallGroqChatAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        BookmarkManager.Contracts.AiTaggingSettingsDto settings,
        CancellationToken ct)
    {
        await Throttle.AwaitThrottleAsync(settings.GroqRequestsPerMinute, ct).ConfigureAwait(false);

        var request = new GroqChatRequest(
            Model: model,
            Temperature: 0.0,
            Messages: new[]
            {
                new GroqMessage("system", systemPrompt),
                new GroqMessage("user", userPrompt)
            });

        var baseUrl = string.IsNullOrWhiteSpace(settings.GroqBaseUrl) ? "https://api.groq.com/openai/v1" : settings.GroqBaseUrl;
        var uri = new Uri($"{baseUrl.TrimEnd('/')}/chat/completions");

        var httpClient = _httpClientFactory.CreateClient(nameof(GroqCompoundSearchService));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.GroqApiKey);
        httpRequest.Content = JsonContent.Create(request, options: JsonOptions);

        using var response = await httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            TimeSpan? retryAfter = null;
            if (response.Headers.RetryAfter?.Delta is { } delta)
            {
                retryAfter = delta;
            }
            else if (response.Headers.RetryAfter?.Date is { } date)
            {
                retryAfter = date - DateTimeOffset.UtcNow;
            }

            await Throttle.RecordRateLimitAsync(retryAfter, ct).ConfigureAwait(false);
            throw new HttpRequestException("Groq rate limit reached.", null, System.Net.HttpStatusCode.TooManyRequests);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Groq API request failed with status {(int)response.StatusCode} {response.StatusCode}: {errorBody}",
                null,
                response.StatusCode);
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var chatResponse = JsonSerializer.Deserialize<GroqChatResponse>(responseJson, JsonOptions);
        var text = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Groq returned an empty search response.");
        }

        return text;
    }

    internal static IReadOnlyList<SearchCandidate> ParseCandidatesJson(string content)
    {
        var jsonPayload = ExtractJsonObject(content);
        if (jsonPayload is null)
        {
            return [];
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<CandidatesResponseJson>(jsonPayload, JsonOptions);
            if (parsed?.Candidates is null)
            {
                return [];
            }

            return parsed.Candidates
                .Where(c => !string.IsNullOrWhiteSpace(c.Url))
                .Select(c => new SearchCandidate(c.Url!, c.Title, c.Why ?? c.Snippet))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    // LLMs frequently wrap JSON in markdown code fences or add a sentence before/after it.
    // Strip fences, then take the substring between the first '{' and the matching last '}'.
    internal static string? ExtractJsonObject(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = text.IndexOf('\n');
            if (firstNewline >= 0)
            {
                text = text[(firstNewline + 1)..];
            }

            var closingFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (closingFence >= 0)
            {
                text = text[..closingFence];
            }

            text = text.Trim();
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end < 0 || end < start)
        {
            return null;
        }

        return text.Substring(start, end - start + 1);
    }

    private static string BuildSearchPrompt(SeriesExtraction extraction, string deadHost)
    {
        var chapterText = string.IsNullOrWhiteSpace(extraction.ChapterNumber) ? "an unspecified chapter" : extraction.ChapterNumber;
        return
            $"Find working links to read {extraction.SeriesName} ({extraction.MediaType}) at chapter {chapterText}.\n" +
            $"The site {deadHost} is permanently offline - never return links on it.\n" +
            "Prefer direct reader pages (the chapter itself), then the series overview page.\n" +
            "Avoid wikis, forums, Reddit, YouTube, social media, news, and store pages.\n" +
            "Return JSON: {\"candidates\": [{\"url\": \"...\", \"why\": \"...\"}]} with at most 5 candidates,\n" +
            "best first.";
    }

    private static string BuildDuckDuckGoQuery(SeriesExtraction extraction)
    {
        var chapterText = string.IsNullOrWhiteSpace(extraction.ChapterNumber) ? string.Empty : $" chapter {extraction.ChapterNumber}";
        return $"{extraction.SeriesName} {extraction.MediaType}{chapterText}".Trim();
    }

    private static string BuildRerankPrompt(SeriesExtraction extraction, string deadHost, IReadOnlyList<string> searchResults)
    {
        var chapterText = string.IsNullOrWhiteSpace(extraction.ChapterNumber) ? "an unspecified chapter" : extraction.ChapterNumber;
        var resultsList = string.Join("\n", searchResults.Select(url => $"- {url}"));
        return
            $"Find working links to read {extraction.SeriesName} ({extraction.MediaType}) at chapter {chapterText}.\n" +
            $"The site {deadHost} is permanently offline - never return links on it.\n" +
            "Here are raw web search results to choose from:\n" +
            $"{resultsList}\n" +
            "Prefer direct reader pages (the chapter itself), then the series overview page.\n" +
            "Avoid wikis, forums, Reddit, YouTube, social media, news, and store pages.\n" +
            "Return JSON: {\"candidates\": [{\"url\": \"...\", \"why\": \"...\"}]} with at most 5 candidates,\n" +
            "best first, chosen only from the results above.";
    }

    private const string SystemPromptForCompound =
        "You are a research assistant that finds working alternative reading pages for manga/manhwa/manhua, " +
        "light novel, webnovel and anime bookmarks whose original site went offline. Always respond with the exact " +
        "JSON contract requested by the user, nothing else.";

    private const string SystemPromptForRerank =
        "You rerank a list of raw web search results to find the best alternative reading page for a manga/manhwa/manhua, " +
        "light novel, webnovel or anime series. Always respond with the exact JSON contract requested by the user, nothing else.";

    private sealed record GroqChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("messages")] IReadOnlyList<GroqMessage> Messages);

    private sealed record GroqMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record GroqChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<GroqChoice>? Choices);

    private sealed record GroqChoice(
        [property: JsonPropertyName("message")] GroqResponseMessage? Message);

    private sealed record GroqResponseMessage(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("content")] string? Content);

    private sealed record CandidatesResponseJson(
        [property: JsonPropertyName("candidates")] List<CandidateJson>? Candidates);

    private sealed record CandidateJson(
        [property: JsonPropertyName("url")] string? Url,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("why")] string? Why,
        [property: JsonPropertyName("snippet")] string? Snippet);
}
