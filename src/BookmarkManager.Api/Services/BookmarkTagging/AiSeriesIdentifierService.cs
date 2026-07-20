using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BookmarkManager.Api.Services;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public sealed record AiSeriesIdentifyPayload(
    Guid Id,
    string Title,
    string? UrlHost,
    string? FolderPath,
    BookmarkTagDomainDto DomainHint);

public sealed record AiSeriesIdentifyCandidate(
    Guid Id,
    string Title,
    string? Url,
    string? FolderPath);

public enum AiSeriesSourceHint
{
    Anime,
    Manga,
    Manhwa,
    Manhua,
    Novel,
    Unknown
}

public sealed record AiSeriesIdentification(
    Guid Id,
    string CanonicalTitle,
    double Confidence,
    AiSeriesSourceHint SourceHint);

public sealed record AiSeriesIdentificationSummary(
    IReadOnlyList<AiSeriesIdentification> Items,
    int FailedChunks,
    IReadOnlyList<string> Messages,
    bool IsRateLimited = false,
    TimeSpan? RetryAfter = null);

public sealed class AiSeriesIdentifierService
{
    private const int MaxPayloadsPerRequest = 50;
    private const int MaxInvalidResponseAttempts = 2;
    private const int MaxTransientFailureAttempts = 3;

    private const string IdentificationInstructions = """
        Return JSON only.
        Return exactly one result for each input id.
        Do not add or drop ids.
        Do not generate tags.
        Identify the canonical series title from noisy bookmark titles.
        Use folder_path and domain_hint as strong prior context.
        If uncertain, return low confidence rather than guessing.
        Response shape: { "items": [ { "id": "00000000-0000-0000-0000-000000000000", "canonicalTitle": "A Monster Who Levels Up", "confidence": 0.91, "sourceHint": "Novel" } ] }.
        Allowed sourceHint values: Anime, Manga, Manhwa, Manhua, Novel, Unknown.
        sourceHint is non-authoritative routing context only; do not fetch or invent tags.
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IAiSeriesIdentificationClient _aiClient;
    private readonly AiTaggingSettingsService? _settings;

    internal AiSeriesIdentifierService()
    {
        _aiClient = null!;
    }

    public AiSeriesIdentifierService(IAiSeriesIdentificationClient aiClient, AiTaggingSettingsService settings)
    {
        _aiClient = aiClient;
        _settings = settings;
    }

    internal AiSeriesIdentifierService(HttpClient httpClient, Uri identifyEndpoint)
    {
        _aiClient = new DirectHttpClientSeriesIdentificationClient(httpClient, identifyEndpoint);
    }

    internal AiSeriesIdentifierService(IHttpClientFactory httpClientFactory, AiTaggingSettingsService settings)
    {
        _aiClient = new GeminiSeriesIdentificationClient(httpClientFactory, settings);
        _settings = settings;
    }

    public IReadOnlyList<AiSeriesIdentifyPayload> BuildPayloads(IEnumerable<AiSeriesIdentifyCandidate> candidates)
        => candidates
            .Select(candidate => new AiSeriesIdentifyPayload(
                candidate.Id,
                candidate.Title,
                TryGetUrlHost(candidate.Url),
                candidate.FolderPath,
                BookmarkTagClassifier.GuessDefaultDomainFromFolderTitle(candidate.FolderPath ?? string.Empty)))
            .ToList();

    public async Task<AiSeriesIdentificationSummary> IdentifyAsync(
        IEnumerable<AiSeriesIdentifyCandidate> candidates,
        CancellationToken cancellationToken)
    {
        var payloads = BuildPayloads(candidates);
        if (payloads.Count == 0)
            return new AiSeriesIdentificationSummary([], 0, [], false, null);

        var results = new List<AiSeriesIdentification>();
        var messages = new List<string>();
        var failedChunks = 0;
        var isRateLimited = false;
        TimeSpan? retryAfter = null;

        var batchSize = MaxPayloadsPerRequest;
        if (_settings != null)
        {
            var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            if (settings.PreferredBatchSize > 0)
            {
                batchSize = settings.PreferredBatchSize;
            }
        }

        var chunks = payloads.Chunk(batchSize)
            .Select(chunk => chunk.ToList())
            .ToList();

        for (var index = 0; index < chunks.Count; index++)
        {
            var chunk = chunks[index];
            messages.Add($"AI identification chunk {index + 1}/{chunks.Count}: sending {chunk.Count} bookmark(s) to AI provider.");

            var isValid = false;
            IReadOnlyList<AiSeriesIdentification> items = [];
            string? error = null;
            IReadOnlyList<string> chunkMessages = [];
            AiProviderRateLimit? rateLimit = null;

            for (var attempt = 1; attempt <= MaxTransientFailureAttempts; attempt++)
            {
                (isValid, items, error, chunkMessages, rateLimit) = await SendAndValidateChunkAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (isValid)
                    break;

                if (rateLimit != null && rateLimit.IsRateLimited)
                {
                    isRateLimited = true;
                    retryAfter = rateLimit.RetryAfter;
                    error = $"429: {rateLimit.Message}";
                }

                var isTransient = IsTransientAiFailure(error) || (rateLimit != null && rateLimit.IsRateLimited);
                var maxAttempts = isTransient ? MaxTransientFailureAttempts : MaxInvalidResponseAttempts;
                if (attempt >= maxAttempts)
                    break;

                var delay = (rateLimit != null && rateLimit.IsRateLimited) 
                    ? (rateLimit.RetryAfter ?? TimeSpan.FromSeconds(60)) 
                    : GetTransientRetryDelay(attempt);

                messages.Add(isTransient
                    ? $"AI identification chunk {index + 1}/{chunks.Count}: rate limit or transient failure ({error}); retrying attempt {attempt + 1}/{maxAttempts} after {delay.TotalSeconds:0}s."
                    : $"AI identification chunk {index + 1}/{chunks.Count}: invalid response ({error}); retrying once.");

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            if (!isValid)
            {
                failedChunks++;
                messages.Add($"Skipped invalid AI identification response chunk {index + 1}/{chunks.Count}: {error}");
                if (isRateLimited)
                {
                    break;
                }
                continue;
            }

            messages.AddRange(chunkMessages);
            results.AddRange(items.Where(item => !string.IsNullOrWhiteSpace(item.CanonicalTitle)));
            messages.Add($"AI identification chunk {index + 1}/{chunks.Count}: identified {items.Count} bookmark(s).");
        }

        return new AiSeriesIdentificationSummary(results, failedChunks, messages, isRateLimited, retryAfter);
    }

    private static bool IsTransientAiFailure(string? error)
    {
        if (error is null) return false;
        if (error.Contains("id", StringComparison.OrdinalIgnoreCase)) return false;
        return error.Contains("503", StringComparison.OrdinalIgnoreCase)
           || error.Contains("429", StringComparison.OrdinalIgnoreCase)
           || error.Contains("timeout", StringComparison.OrdinalIgnoreCase)
           || error.Contains("timed out", StringComparison.OrdinalIgnoreCase)
           || error.Contains("temporarily", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan GetTransientRetryDelay(int attempt)
        => attempt switch
        {
            1 => TimeSpan.FromSeconds(15),
            _ => TimeSpan.FromSeconds(30)
        };

    private async Task<(bool IsValid, IReadOnlyList<AiSeriesIdentification> Items, string? Error, IReadOnlyList<string> Messages, AiProviderRateLimit? RateLimit)> SendAndValidateChunkAsync(
        IReadOnlyList<AiSeriesIdentifyPayload> payloads,
        CancellationToken cancellationToken)
    {
        var request = new AiSeriesIdentifyRequest(
            IdentificationInstructions,
            payloads.Select(payload => new AiSeriesIdentifyRequestItem(
                payload.Id,
                payload.Title,
                payload.UrlHost,
                payload.FolderPath,
                payload.DomainHint)).ToList());

        string json;
        AiProviderRateLimit? rateLimit = null;
        try
        {
            var response = await _aiClient.IdentifyAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.RateLimit != null && response.RateLimit.IsRateLimited)
            {
                rateLimit = response.RateLimit;
                return (false, [], $"429: {response.RateLimit.Message}", [], rateLimit);
            }
            json = response.Json;
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return (false, [], $"AI request timed out ({ex.Message})", [], null);
        }
        catch (HttpRequestException ex)
        {
            var status = ex.StatusCode is null ? string.Empty : $" with {(int)ex.StatusCode} {ex.StatusCode}";
            return (false, [], $"AI request failed{status} ({ex.Message})", [], null);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return (false, [], $"invalid JSON ({ex.Message})", [], null);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("items", out var itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
                return (false, [], "response did not contain an items array", [], null);

            var expectedIds = payloads.Select(payload => payload.Id).ToHashSet();
            var seenIds = new HashSet<Guid>();
            var items = new List<AiSeriesIdentification>();
            var messages = new List<string>();

            foreach (var itemElement in itemsElement.EnumerateArray())
            {
                if (!itemElement.TryGetProperty("id", out var idElement))
                {
                    messages.Add("Warning: AI returned item with missing id property.");
                    continue;
                }
                if (!idElement.TryGetGuid(out var id))
                {
                    var idStr = idElement.GetString() ?? idElement.ToString();
                    messages.Add($"Warning: AI returned item with invalid id '{idStr}'.");
                    continue;
                }

                if (!seenIds.Add(id))
                    return (false, [], $"duplicate id {id}", [], null);

                if (!expectedIds.Contains(id))
                    return (false, [], $"extra id {id}", [], null);

                var canonicalTitle = itemElement.TryGetProperty("canonicalTitle", out var titleElement) && titleElement.ValueKind == JsonValueKind.String
                    ? titleElement.GetString()?.Trim() ?? string.Empty
                    : string.Empty;

                if (string.IsNullOrWhiteSpace(canonicalTitle))
                {
                    messages.Add($"Warning: AI returned item for id '{id}' with empty canonicalTitle.");
                    continue;
                }

                var confidence = itemElement.TryGetProperty("confidence", out var confidenceElement) && confidenceElement.TryGetDouble(out var value)
                    ? value
                    : double.NaN;
                if (confidence is < 0 or > 1 || double.IsNaN(confidence))
                    return (false, [], $"invalid confidence for id {id}", [], null);

                var sourceHint = AiSeriesSourceHint.Unknown;
                if (itemElement.TryGetProperty("sourceHint", out var sourceHintElement) && sourceHintElement.ValueKind == JsonValueKind.String)
                    Enum.TryParse(sourceHintElement.GetString(), ignoreCase: true, out sourceHint);

                items.Add(new AiSeriesIdentification(id, canonicalTitle, confidence, sourceHint));
            }

            if (seenIds.Count != expectedIds.Count)
            {
                var missingIds = expectedIds.Except(seenIds).ToList();
                return (false, [], $"missing id(s): {string.Join(", ", missingIds)}", [], null);
            }

            return (true, items, null, messages, null);
        }
    }

    private static string? TryGetUrlHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        return string.IsNullOrWhiteSpace(uri.Host) ? null : uri.Host.ToLowerInvariant();
    }

    private sealed class DirectHttpClientSeriesIdentificationClient : IAiSeriesIdentificationClient
    {
        private readonly HttpClient _httpClient;
        private readonly Uri _identifyEndpoint;

        public DirectHttpClientSeriesIdentificationClient(HttpClient httpClient, Uri identifyEndpoint)
        {
            _httpClient = httpClient;
            _identifyEndpoint = identifyEndpoint;
        }

        public async Task<AiProviderResponse> IdentifyAsync(AiSeriesIdentifyRequest request, CancellationToken cancellationToken)
        {
            using var directResponse = await _httpClient.PostAsJsonAsync(_identifyEndpoint, request, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            
            if (directResponse.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                TimeSpan? retryAfter = null;
                if (directResponse.Headers.RetryAfter != null)
                {
                    if (directResponse.Headers.RetryAfter.Delta.HasValue)
                    {
                        retryAfter = directResponse.Headers.RetryAfter.Delta.Value;
                    }
                    else if (directResponse.Headers.RetryAfter.Date.HasValue)
                    {
                        retryAfter = directResponse.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                    }
                }
                return new AiProviderResponse(
                    string.Empty,
                    new AiProviderRateLimit(true, retryAfter ?? TimeSpan.FromSeconds(60), "Rate limit reached."));
            }

            directResponse.EnsureSuccessStatusCode();
            var json = await directResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return new AiProviderResponse(json);
        }

        // The Settings "Test key" flow only runs against the DI-registered OpenRouter client.
        public Task<TestAiKeyResponse> TestConnectionAsync(TestAiKeyRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new TestAiKeyResponse { Success = false, Message = "Key testing is not supported for this provider." });
    }

    private sealed class GeminiSeriesIdentificationClient : IAiSeriesIdentificationClient
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly AiTaggingSettingsService _settings;

        public GeminiSeriesIdentificationClient(IHttpClientFactory httpClientFactory, AiTaggingSettingsService settings)
        {
            _httpClientFactory = httpClientFactory;
            _settings = settings;
        }

        public async Task<AiProviderResponse> IdentifyAsync(AiSeriesIdentifyRequest request, CancellationToken cancellationToken)
        {
            var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            if (!settings.Enabled)
                throw new InvalidOperationException("Gemini AI auto-tagging is disabled in Settings.");

            if (string.IsNullOrWhiteSpace(settings.ApiKey))
                throw new InvalidOperationException("Gemini API key is required before AI auto-tagging can identify series titles.");

            var endpoint = string.IsNullOrWhiteSpace(settings.Endpoint)
                ? "https://generativelanguage.googleapis.com/v1beta"
                : settings.Endpoint.Trim().TrimEnd('/');
            var model = string.IsNullOrWhiteSpace(settings.Model) ? "gemini-2.5-flash" : settings.Model.Trim();
            var uri = new Uri($"{endpoint}/models/{Uri.EscapeDataString(model)}:generateContent?key={Uri.EscapeDataString(settings.ApiKey)}");
            var prompt = JsonSerializer.Serialize(request, JsonOptions);
            var geminiRequest = new GeminiGenerateContentRequest(
                [new GeminiContent([new GeminiPart($"{request.Instructions}\n\nInput JSON:\n{prompt}")])],
                new GeminiGenerationConfig("application/json"));

            var http = _httpClientFactory.CreateClient(nameof(AiSeriesIdentifierService));
            using var response = await http.PostAsJsonAsync(uri, geminiRequest, JsonOptions, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var geminiJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var geminiResponse = JsonSerializer.Deserialize<GeminiGenerateContentResponse>(geminiJson, JsonOptions);
            var text = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(text))
                throw new InvalidOperationException("Gemini returned an empty AI identification response.");

            return new AiProviderResponse(text);
        }

        // The Settings "Test key" flow only runs against the DI-registered OpenRouter client.
        public Task<TestAiKeyResponse> TestConnectionAsync(TestAiKeyRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new TestAiKeyResponse { Success = false, Message = "Key testing is not supported for this provider." });
    }

    private sealed record GeminiGenerateContentRequest(
        IReadOnlyList<GeminiContent> Contents,
        GeminiGenerationConfig GenerationConfig);

    private sealed record GeminiGenerationConfig(string ResponseMimeType);

    private sealed record GeminiGenerateContentResponse(IReadOnlyList<GeminiCandidate>? Candidates);

    private sealed record GeminiCandidate(GeminiContent? Content);

    private sealed record GeminiContent(IReadOnlyList<GeminiPart>? Parts);

    private sealed record GeminiPart(string? Text);
}
