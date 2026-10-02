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
    private readonly AiRequestThrottle _throttle;
    private readonly ILogger<GroqCompoundSearchService> _logger;
    private readonly GeminiGroundedSearchService? _gemini;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public GroqCompoundSearchService(
        IHttpClientFactory httpClientFactory,
        AiTaggingSettingsService settings,
        IDuckDuckGoSearchService duckDuckGo,
        ILogger<GroqCompoundSearchService> logger,
        AiRequestThrottle? throttle = null,
        GeminiGroundedSearchService? gemini = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _duckDuckGo = duckDuckGo ?? throw new ArgumentNullException(nameof(duckDuckGo));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _throttle = throttle ?? new AiRequestThrottle();
        _gemini = gemini;
    }

    public async Task<IReadOnlyList<SearchCandidate>> SearchAsync(
        SeriesExtraction extraction, string deadHost, CancellationToken ct, string? preferredHost = null, bool restrictToPreferredHost = false)
        => (await SearchWithDiagnosticsAsync(extraction, deadHost, new SearchRunContext(), ct, preferredHost, restrictToPreferredHost)).Candidates;

    public async Task<SearchOutcome<SearchCandidate>> SearchWithDiagnosticsAsync(
        SeriesExtraction extraction, string deadHost, SearchRunContext run, CancellationToken ct,
        string? preferredHost = null, bool restrictToPreferredHost = false)
    {
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentException.ThrowIfNullOrWhiteSpace(deadHost);
        restrictToPreferredHost &= !string.IsNullOrWhiteSpace(preferredHost);
        var settings = await _settings.GetAsync(ct).ConfigureAwait(false);
        var stages = new List<SearchStage>();
        IReadOnlyList<SearchCandidate> Shape(IReadOnlyList<SearchCandidate> candidates)
        {
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            var filtered = ApplyHostShaping(SearchCandidateFilter.Filter(candidates, deadHost), preferredHost, restrictToPreferredHost);
            var reason = candidates.Count > 0 && filtered.Count == 0 ? "all candidates excluded by URL/host rules" : null;
            _logger.LogInformation("Migration search provider {Provider} elapsed {ElapsedMs} ms candidates {CandidateCount} input {InputCount} reason {FailureReason}",
                "Candidate filter", elapsed.ElapsedMilliseconds, filtered.Count, candidates.Count, reason ?? "success");
            if (reason != null) stages.Add(new("Candidate filter", 0, reason));
            return filtered;
        }

        // Provider is chosen at search time from settings; "Gemini" (Google Search grounding) is the
        // default because Groq's compound models were decommissioned. Either primary failure falls
        // through to the same DDG/Yahoo HTML chain below.
        var provider = string.IsNullOrWhiteSpace(settings.MigrationSearchProvider) ? "Gemini" : settings.MigrationSearchProvider;
        var useGemini = string.Equals(provider, "Gemini", StringComparison.OrdinalIgnoreCase) && _gemini is not null;
        if (useGemini)
        {
            var gemini = await _gemini!.SearchWithDiagnosticsAsync(extraction, deadHost, run, ct, preferredHost, restrictToPreferredHost);
            stages.AddRange(gemini.Stages);
            var geminiCandidates = Shape(gemini.Candidates);
            if (geminiCandidates.Count > 0) return new(geminiCandidates, stages);
        }
        else
        {
            var model = string.IsNullOrWhiteSpace(settings.MigrationSearchModel) ? "groq/compound-mini" : settings.MigrationSearchModel;
            var publicGroq = string.IsNullOrWhiteSpace(settings.GroqBaseUrl) ||
                Uri.TryCreate(settings.GroqBaseUrl, UriKind.Absolute, out var baseUri) && baseUri.Host.Equals("api.groq.com", StringComparison.OrdinalIgnoreCase);
            var retired = publicGroq && model is "groq/compound-mini" or "groq/compound";
            if (!string.IsNullOrWhiteSpace(settings.GroqApiKey) && !retired)
            {
                var compound = await run.ExecuteAsync("Groq compound", TimeSpan.FromSeconds(12), async token =>
                    Shape(await SearchWithCompoundAsync(extraction, deadHost, preferredHost, restrictToPreferredHost, settings, token)), _logger, ct);
                stages.AddRange(compound.Stages);
                if (compound.Candidates.Count > 0) return new(compound.Candidates, stages);
            }
            else
            {
                var reason = retired ? "model decommissioned (2026-09-21)" : "API key not configured";
                stages.Add(new("Groq compound", 0, reason));
                _logger.LogInformation("Migration search provider {Provider} elapsed {ElapsedMs} ms candidates {CandidateCount} reason {FailureReason}",
                    "Groq compound", 0, 0, reason);
            }
        }

        var query = BuildDuckDuckGoQuery(extraction);
        if (restrictToPreferredHost) query += $" site:{preferredHost}";
        var html = await _duckDuckGo.SearchWithDiagnosticsAsync(query, deadHost, run, ct);
        stages.AddRange(html.Stages);
        var raw = Shape(html.Candidates.Select(url => new SearchCandidate(url, null, null)).ToArray());
        if (raw.Count == 0 || string.IsNullOrWhiteSpace(settings.GroqApiKey)) return new(raw, stages);

        var ranked = await run.ExecuteAsync("Groq rerank", TimeSpan.FromSeconds(8), async token =>
        {
            var rerankModel = string.IsNullOrWhiteSpace(settings.GroqModel) ? "openai/gpt-oss-120b" : settings.GroqModel;
            var prompt = BuildRerankPrompt(extraction, deadHost, preferredHost, restrictToPreferredHost, raw.Select(c => c.Url).ToArray());
            var content = await CallGroqChatAsync(rerankModel, SystemPromptForRerank, prompt, settings, token);
            // A reranker may only choose supplied URLs. Preserve usable raw results if its output is unusable.
            var allowed = raw.Select(c => UrlComparisonNormalizer.Normalize(c.Url)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Shape(ParseSearchResponse(content).Where(c => allowed.Contains(UrlComparisonNormalizer.Normalize(c.Url))).ToArray());
        }, _logger, ct);
        stages.AddRange(ranked.Stages);
        return new(ranked.Candidates.Count > 0 ? ranked.Candidates : raw, stages);
    }

    /// <summary>
    /// When <paramref name="restrictToPreferredHost"/>, drops every candidate not on that host
    /// (the user picked the target domain, so anything else is noise). Otherwise just moves
    /// same-host candidates to the front, preserving relative order otherwise.
    /// </summary>
    private static IReadOnlyList<SearchCandidate> ApplyHostShaping(
        IReadOnlyList<SearchCandidate> candidates, string? preferredHost, bool restrictToPreferredHost)
    {
        if (string.IsNullOrWhiteSpace(preferredHost) || candidates.Count == 0)
        {
            return candidates;
        }

        bool MatchesPreferredHost(SearchCandidate c) =>
            Uri.TryCreate(c.Url, UriKind.Absolute, out var uri) &&
            (uri.Host.Equals(preferredHost, StringComparison.OrdinalIgnoreCase) ||
             uri.Host.EndsWith("." + preferredHost, StringComparison.OrdinalIgnoreCase));

        if (restrictToPreferredHost)
        {
            return candidates.Where(MatchesPreferredHost).ToList();
        }

        return candidates.OrderByDescending(MatchesPreferredHost).ToList();
    }

    private async Task<IReadOnlyList<SearchCandidate>> SearchWithCompoundAsync(
        SeriesExtraction extraction,
        string deadHost,
        string? preferredHost,
        bool restrictToPreferredHost,
        BookmarkManager.Contracts.AiTaggingSettingsDto settings,
        CancellationToken ct)
    {
        var model = string.IsNullOrWhiteSpace(settings.MigrationSearchModel) ? "groq/compound-mini" : settings.MigrationSearchModel;
        var prompt = BuildSearchPrompt(extraction, deadHost, preferredHost, restrictToPreferredHost);

        var content = await CallGroqChatAsync(model, SystemPromptForCompound, prompt, settings, ct).ConfigureAwait(false);
        return ParseSearchResponse(content);
    }

    private async Task<string> CallGroqChatAsync(
        string model,
        string systemPrompt,
        string userPrompt,
        BookmarkManager.Contracts.AiTaggingSettingsDto settings,
        CancellationToken ct)
    {
        await _throttle.AwaitThrottleAsync(settings.GroqRequestsPerMinute, ct).ConfigureAwait(false);

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

            await _throttle.RecordRateLimitAsync(retryAfter, ct).ConfigureAwait(false);
            throw new HttpRequestException("Groq rate limit reached.", null, System.Net.HttpStatusCode.TooManyRequests);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Groq API request failed with status {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var responseJson = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var chatResponse = JsonSerializer.Deserialize<GroqChatResponse>(responseJson, JsonOptions);
        var text = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new SearchResponseException("empty response");
        }

        return text;
    }

    private static IReadOnlyList<SearchCandidate> ParseSearchResponse(string content)
    {
        var json = ExtractJsonObject(content);
        if (json is null) throw new SearchResponseException("invalid candidate response");
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array)
            throw new SearchResponseException("missing candidates array");
        var parsed = JsonSerializer.Deserialize<CandidatesResponseJson>(json, JsonOptions)!;
        return parsed.Candidates!.Where(c => !string.IsNullOrWhiteSpace(c.Url))
            .Select(c => new SearchCandidate(c.Url!, c.Title, c.Why ?? c.Snippet)).ToArray();
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
    // Strip fences, then take the first complete `{...}` object (brace-matched). Spanning from
    // the first '{' to the last '}' silently breaks when the model emits two objects.
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
        if (start < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escape = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (c == '\\')
                {
                    escape = true;
                    continue;
                }

                if (c == '"')
                    inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return text.Substring(start, i - start + 1);
                    break;
            }
        }

        return null;
    }

    private static string BuildSearchPrompt(SeriesExtraction extraction, string deadHost, string? preferredHost, bool restrictToPreferredHost)
    {
        var chapterText = string.IsNullOrWhiteSpace(extraction.ChapterNumber) ? "an unspecified chapter" : extraction.ChapterNumber;
        var preferredHostLine = BuildPreferredHostLine(preferredHost, restrictToPreferredHost);
        return
            $"Find working links to read {extraction.SeriesName} ({extraction.MediaType}) at chapter {chapterText}.\n" +
            $"The site {deadHost} is permanently offline - never return links on it.\n" +
            preferredHostLine +
            "Prefer direct reader pages (the chapter itself), then the series overview page.\n" +
            "Avoid wikis, forums, Reddit, YouTube, social media, news, and store pages.\n" +
            "Return JSON: {\"candidates\": [{\"url\": \"...\", \"why\": \"...\"}]} with at most 5 candidates,\n" +
            "best first.";
    }

    private static string BuildPreferredHostLine(string? preferredHost, bool restrictToPreferredHost)
    {
        if (string.IsNullOrWhiteSpace(preferredHost))
        {
            return string.Empty;
        }

        return restrictToPreferredHost
            ? $"ONLY return results on {preferredHost} - the user already picked this as the migration target. " +
              "Do not return any other domain, even if you can't find the series there.\n"
            : $"Strongly prefer {preferredHost} if it has this series - other bookmarks from this same batch were just " +
              "migrated there, and manga/anime aggregator sites that host one series usually host most others too.\n";
    }

    private static string BuildDuckDuckGoQuery(SeriesExtraction extraction)
    {
        var chapterText = string.IsNullOrWhiteSpace(extraction.ChapterNumber) ? string.Empty : $" chapter {extraction.ChapterNumber}";
        return $"{extraction.SeriesName} {extraction.MediaType}{chapterText}".Trim();
    }

    private static string BuildRerankPrompt(
        SeriesExtraction extraction, string deadHost, string? preferredHost, bool restrictToPreferredHost, IReadOnlyList<string> searchResults)
    {
        var chapterText = string.IsNullOrWhiteSpace(extraction.ChapterNumber) ? "an unspecified chapter" : extraction.ChapterNumber;
        var resultsList = string.Join("\n", searchResults.Select(url => $"- {url}"));
        var preferredHostLine = BuildPreferredHostLine(preferredHost, restrictToPreferredHost);
        return
            $"Find working links to read {extraction.SeriesName} ({extraction.MediaType}) at chapter {chapterText}.\n" +
            $"The site {deadHost} is permanently offline - never return links on it.\n" +
            "Here are raw web search results to choose from:\n" +
            $"{resultsList}\n" +
            preferredHostLine +
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
