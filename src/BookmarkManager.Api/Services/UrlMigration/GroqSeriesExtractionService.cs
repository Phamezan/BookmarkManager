using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>One bookmark passed into <see cref="GroqSeriesExtractionService.ExtractBatchAsync"/>.</summary>
public sealed record SeriesExtractionRequestItem(string Title, string Url, string? Category);

/// <summary>
/// Groq structured extraction for the URL Migrator "extract" stage. Batches up to 25 bookmarks
/// per call (one JSON array in, one JSON array out) using the same key/model/RPM settings and
/// <see cref="AiRequestThrottle"/> pattern as <see cref="GroqSeriesIdentificationClient"/>.
/// Parses defensively: strips code fences, validates ids round-trip. Any item that can't be
/// mapped back to a valid, well-formed result falls back individually to
/// <see cref="SeriesExtractionFallback"/> with <c>UsedFallback = true</c>. If the whole request
/// fails (missing key, HTTP error, rate limit, unparsable response), every item in the batch
/// falls back.
/// </summary>
public sealed class GroqSeriesExtractionService : ISeriesExtractionService
{
    private const int MaxBatchSize = 25;

    private const string SystemPrompt = """
        You extract reading-progress info from browser bookmarks of manga/manhwa/manhua,
        light novel, webnovel and anime sites. For each item you receive {id, title, url,
        category}. Return a JSON array of {id, series, chapter, mediaType}.
        - "series": canonical series name, no site branding, no "chapter 112", no "read online".
        - "chapter": the chapter/episode the user was at. Prefer the URL path over the title
          (e.g. /solo-leveling/chapter-110 -> "110"). Null if absent from both.
        - "mediaType": one of manga, manhwa, manhua, lightnovel, webnovel, anime, unknown.
        Return only the JSON array.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AiTaggingSettingsService _settings;
    private readonly AiRequestThrottle _throttle;
    private readonly ILogger<GroqSeriesExtractionService> _logger;

    public GroqSeriesExtractionService(
        IHttpClientFactory httpClientFactory,
        AiTaggingSettingsService settings,
        ILogger<GroqSeriesExtractionService> logger,
        AiRequestThrottle? throttle = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _throttle = throttle ?? new AiRequestThrottle();
    }

    public async Task<SeriesExtraction> ExtractAsync(string title, string url, string? category, CancellationToken cancellationToken)
    {
        var results = await ExtractBatchAsync([new SeriesExtractionRequestItem(title, url, category)], cancellationToken)
            .ConfigureAwait(false);
        return results[0];
    }

    /// <summary>
    /// Batches <paramref name="items"/> into groups of up to 25 and resolves each. The result
    /// list is the same length and order as <paramref name="items"/>.
    /// </summary>
    public async Task<IReadOnlyList<SeriesExtraction>> ExtractBatchAsync(
        IReadOnlyList<SeriesExtractionRequestItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return [];

        var results = new SeriesExtraction?[items.Count];

        foreach (var chunk in Enumerable.Range(0, items.Count).Chunk(MaxBatchSize))
        {
            await ExtractChunkAsync(items, chunk, results, cancellationToken).ConfigureAwait(false);
        }

        return results.Select(r => r!).ToList();
    }

    private async Task ExtractChunkAsync(
        IReadOnlyList<SeriesExtractionRequestItem> allItems,
        IReadOnlyList<int> chunkIndexes,
        SeriesExtraction?[] results,
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(settings.GroqApiKey))
                throw new InvalidOperationException("Groq API key is required for series extraction.");

            await _throttle.AwaitThrottleAsync(settings.GroqRequestsPerMinute, cancellationToken).ConfigureAwait(false);

            var payload = chunkIndexes.Select(index => new GroqExtractionRequestItem(
                index,
                allItems[index].Title,
                allItems[index].Url,
                allItems[index].Category)).ToList();

            var itemsJson = JsonSerializer.Serialize(payload, JsonOptions);

            var groqRequest = new GroqChatRequest(
                Model: string.IsNullOrWhiteSpace(settings.GroqModel) ? "llama-3.3-70b-versatile" : settings.GroqModel,
                Temperature: 0.0,
                Messages:
                [
                    new GroqMessage("system", SystemPrompt),
                    new GroqMessage("user", itemsJson)
                ]);

            var baseUrl = string.IsNullOrWhiteSpace(settings.GroqBaseUrl) ? "https://api.groq.com/openai/v1" : settings.GroqBaseUrl;
            var uri = new Uri($"{baseUrl.TrimEnd('/')}/chat/completions");

            var httpClient = _httpClientFactory.CreateClient(nameof(GroqSeriesExtractionService));

            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.GroqApiKey);
            httpRequest.Content = JsonContent.Create(groqRequest, options: JsonOptions);

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                TimeSpan? retryAfter = null;
                if (response.Headers.RetryAfter != null)
                {
                    if (response.Headers.RetryAfter.Delta.HasValue)
                        retryAfter = response.Headers.RetryAfter.Delta.Value;
                    else if (response.Headers.RetryAfter.Date.HasValue)
                        retryAfter = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                }

                await _throttle.RecordRateLimitAsync(retryAfter, cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("Groq rate limit reached during series extraction.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException(
                    $"Groq extraction request failed with status {(int)response.StatusCode} {response.StatusCode}: {errorBody}",
                    null,
                    response.StatusCode);
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var chatResponse = JsonSerializer.Deserialize<GroqChatResponse>(responseJson, JsonOptions);
            var text = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content;

            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("Groq returned an empty series extraction response.");

            ApplyParsedResults(text, allItems, chunkIndexes, results);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Groq series extraction failed for a batch of {Count} item(s); falling back to heuristic extraction.",
                chunkIndexes.Count);

            foreach (var index in chunkIndexes)
            {
                results[index] ??= BuildFallback(allItems[index]);
            }
        }
    }

    private static void ApplyParsedResults(
        string text,
        IReadOnlyList<SeriesExtractionRequestItem> allItems,
        IReadOnlyList<int> chunkIndexes,
        SeriesExtraction?[] results)
    {
        var cleaned = StripCodeFences(text);

        // Let JsonException/InvalidOperationException bubble up to the caller's catch block,
        // which falls back the whole chunk - a malformed top-level response can't be trusted
        // per-item.
        using var document = JsonDocument.Parse(cleaned);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Groq series extraction response was not a JSON array.");

        var expectedIndexes = new HashSet<int>(chunkIndexes);
        var seenIndexes = new HashSet<int>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id))
                continue; // Can't map this entry to a request item; the item stays null and is filled by fallback below.

            if (!expectedIndexes.Contains(id) || !seenIndexes.Add(id))
                continue; // Out-of-range or duplicate id ("id mismatch") - ignore; fallback fills the real item.

            var item = allItems[id];
            var series = element.TryGetProperty("series", out var seriesElement) && seriesElement.ValueKind == JsonValueKind.String
                ? seriesElement.GetString()?.Trim()
                : null;

            if (string.IsNullOrWhiteSpace(series))
            {
                results[id] = BuildFallback(item);
                continue;
            }

            var chapter = element.TryGetProperty("chapter", out var chapterElement) && chapterElement.ValueKind == JsonValueKind.String
                ? chapterElement.GetString()?.Trim()
                : null;
            var mediaType = element.TryGetProperty("mediaType", out var mediaTypeElement) && mediaTypeElement.ValueKind == JsonValueKind.String
                ? mediaTypeElement.GetString()?.Trim().ToLowerInvariant()
                : null;

            results[id] = new SeriesExtraction(
                series!,
                string.IsNullOrWhiteSpace(chapter) ? null : chapter,
                string.IsNullOrWhiteSpace(mediaType) ? "unknown" : mediaType,
                UsedFallback: false);
        }

        // Any expected id absent from the response (or skipped above) falls back individually.
        foreach (var index in chunkIndexes)
        {
            results[index] ??= BuildFallback(allItems[index]);
        }
    }

    private static SeriesExtraction BuildFallback(SeriesExtractionRequestItem item)
        => SeriesExtractionFallback.Extract(item.Title, item.Url, item.Category);

    private static string StripCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline >= 0)
            trimmed = trimmed[(firstNewline + 1)..];

        var closingFenceIndex = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        if (closingFenceIndex >= 0)
            trimmed = trimmed[..closingFenceIndex];

        return trimmed.Trim();
    }

    private sealed record GroqExtractionRequestItem(
        [property: JsonPropertyName("id")] int Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("category")] string? Category);

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
}
