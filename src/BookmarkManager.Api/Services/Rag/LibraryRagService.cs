using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.Embedding;
using BookmarkManager.Api.Services.Rerank;
using BookmarkManager.Api.Services.Search;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.Rag;

/// <summary>Retrieval-augmented Library assistant. Embeds the user's question, pulls the nearest catalog
/// entries via <see cref="IHybridSearchService"/> (dense vector + FTS5/BM25 keyword, fused with RRF),
/// and asks an OpenAI-compatible chat model (same request/response shape as
/// <c>GroqSeriesIdentificationClient</c>) to answer grounded on that context.</summary>
public sealed class LibraryRagService : ILibraryRagService
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client so the RAG LLM call gets its own pipeline.</summary>
    public const string HttpClientName = nameof(LibraryRagService);

    private const int MaxHistoryTurns = 6;

    /// <summary>Non-negotiable grounding rules appended after the user-configurable persona so the
    /// assistant stays anchored to the retrieved catalog regardless of how the persona is edited.</summary>
    private const string GroundingRules =
        "Answer the user's question using ONLY the numbered catalog entries provided as context. Recommend "
        + "specific titles from that context by name, briefly explaining why each fits. If the context "
        + "does not contain anything relevant, say so plainly instead of inventing titles. Respond in "
        + "concise Markdown.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IEmbeddingService _embeddingService;
    private readonly IHybridSearchService _hybridSearch;
    private readonly IRerankerService _reranker;
    private readonly AppDbContext _db;
    private readonly AiTaggingSettingsService _settings;
    private readonly AiRequestThrottle _throttle;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LibraryRagService> _logger;

    public LibraryRagService(
        IEmbeddingService embeddingService,
        IHybridSearchService hybridSearch,
        IRerankerService reranker,
        AppDbContext db,
        AiTaggingSettingsService settings,
        AiRequestThrottle throttle,
        IHttpClientFactory httpClientFactory,
        ILogger<LibraryRagService> logger)
    {
        _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
        _hybridSearch = hybridSearch ?? throw new ArgumentNullException(nameof(hybridSearch));
        _reranker = reranker ?? throw new ArgumentNullException(nameof(reranker));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _throttle = throttle ?? throw new ArgumentNullException(nameof(throttle));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<LibraryChatResponseDto> ChatAsync(LibraryChatRequestDto request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Message))
            throw new ArgumentException("A chat message is required.", nameof(request));

        if (!_embeddingService.IsReady)
        {
            return new LibraryChatResponseDto(
                "The semantic search model is not ready yet. Please try again once catalog embeddings have finished loading.",
                Array.Empty<LibraryRecommendedSeriesDto>());
        }

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.RagApiKey))
        {
            return new LibraryChatResponseDto(
                "No Library assistant API key is configured yet. Open **Settings → AI → Library AI assistant** "
                + "and paste a key (e.g. a free Groq key from console.groq.com/keys) to start chatting.",
                Array.Empty<LibraryRecommendedSeriesDto>());
        }

        // No post-hoc RagMinSimilarity cosine floor here: RRF fusion + the RagTopK cut already do the
        // filtering (see IHybridSearchService), and a keyword-only hit can legitimately score below
        // that floor on cosine while still being the right answer for an exact-title/proper-noun query.
        // Stage 1 retrieves a wide pool (RerankCandidatePool) so stage 2 has real material to reorder;
        // RerankPipeline falls back to the first RagTopK of this hybrid order untouched if the reranker
        // isn't ready or fails.
        var queryVector = await _embeddingService.EmbedQueryAsync(request.Message, cancellationToken).ConfigureAwait(false);
        var hits = await _hybridSearch
            .SearchAsync(request.Message, queryVector, RerankConstants.RerankCandidatePool, cancellationToken)
            .ConfigureAwait(false);

        var cards = await LoadRecommendedSeriesAsync(request.Message, hits, cancellationToken).ConfigureAwait(false);
        if (cards.Count == 0)
        {
            return new LibraryChatResponseDto(
                "I couldn't find anything in your catalog that matches that. Try rephrasing, or browse the catalog directly.",
                Array.Empty<LibraryRecommendedSeriesDto>());
        }

        var markdown = await CompleteAsync(settings, request, cards, cancellationToken).ConfigureAwait(false);
        return new LibraryChatResponseDto(markdown, cards);
    }

    /// <summary>Loads catalog rows for the hybrid search hits, reorders them through the stage-2 reranker
    /// (<see cref="RerankPipeline"/>, which falls back to the hybrid order unchanged if the reranker isn't
    /// ready), and returns the top <see cref="EmbeddingConstants.RagTopK"/> cards. The displayed
    /// <c>Score</c> stays the stage-1 cosine similarity - reranking only changes ordering/selection, not
    /// what's shown as the match percentage.</summary>
    private async Task<IReadOnlyList<LibraryRecommendedSeriesDto>> LoadRecommendedSeriesAsync(
        string query,
        IReadOnlyList<(Guid Id, float Score, double RrfScore)> hits,
        CancellationToken cancellationToken)
    {
        if (hits.Count == 0)
            return Array.Empty<LibraryRecommendedSeriesDto>();

        var hybridOrderIds = hits.Select(h => h.Id).ToList();
        var entriesById = await _db.LibraryCatalogEntries
            .AsNoTracking()
            .Where(e => hybridOrderIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);

        var textById = entriesById.ToDictionary(kv => kv.Key, kv => RerankDocumentText.Build(kv.Value));
        var rerank = await RerankPipeline
            .ApplyAsync(_reranker, query, hybridOrderIds, textById, EmbeddingConstants.RagTopK, _logger, cancellationToken)
            .ConfigureAwait(false);

        var scoreById = hits.ToDictionary(h => h.Id, h => h.Score);

        var cards = new List<LibraryRecommendedSeriesDto>(rerank.OrderedIds.Count);
        foreach (var id in rerank.OrderedIds)
        {
            if (!entriesById.TryGetValue(id, out var entry))
                continue;

            cards.Add(new LibraryRecommendedSeriesDto(
                entry.Provider,
                entry.ProviderId,
                entry.Title,
                entry.CoverImageUrl,
                entry.Synopsis,
                LibraryCatalogEntry.SplitList(entry.Genres),
                entry.MediaType,
                entry.SourceUrl,
                scoreById.GetValueOrDefault(id)));
        }

        return cards;
    }

    /// <summary>Builds the grounded prompt and calls the OpenAI-compatible chat endpoint.</summary>
    private async Task<string> CompleteAsync(
        AiTaggingSettingsDto settings,
        LibraryChatRequestDto request,
        IReadOnlyList<LibraryRecommendedSeriesDto> cards,
        CancellationToken cancellationToken)
    {
        var persona = string.IsNullOrWhiteSpace(settings.RagSystemPrompt)
            ? AiTaggingSettingsDto.RagDefaultSystemPrompt
            : settings.RagSystemPrompt;
        var messages = BuildMessages(persona, request, cards);

        var hasFallback = !string.IsNullOrWhiteSpace(settings.RagFallbackApiKey);

        var primary = await CallProviderAsync(
            baseUrl: string.IsNullOrWhiteSpace(settings.RagBaseUrl) ? "https://api.groq.com/openai/v1" : settings.RagBaseUrl,
            model: string.IsNullOrWhiteSpace(settings.RagModel) ? "llama-3.3-70b-versatile" : settings.RagModel,
            apiKey: settings.RagApiKey,
            rpm: settings.RagRequestsPerMinute,
            messages: messages,
            isFallback: false,
            cancellationToken).ConfigureAwait(false);

        // Only fail over to the secondary provider when the primary was rate-limited or hit a transient
        // server error - not on a bad key/model, which the fallback can't fix and which the user must correct.
        var shouldFailOver = hasFallback && primary.Outcome is CallOutcome.RateLimited or CallOutcome.ServerError;
        if (!shouldFailOver)
            return primary.Message;

        _logger.LogInformation("RAG primary provider {Outcome}; failing over to secondary provider.", primary.Outcome);
        var fallback = await CallProviderAsync(
            baseUrl: string.IsNullOrWhiteSpace(settings.RagFallbackBaseUrl) ? "https://integrate.api.nvidia.com/v1" : settings.RagFallbackBaseUrl,
            model: string.IsNullOrWhiteSpace(settings.RagFallbackModel) ? "meta/llama-3.3-70b-instruct" : settings.RagFallbackModel,
            apiKey: settings.RagFallbackApiKey,
            rpm: settings.RagRequestsPerMinute,
            messages: messages,
            isFallback: true,
            cancellationToken).ConfigureAwait(false);

        // If the fallback also failed, surface the primary's (usually clearer) rate-limit message.
        return fallback.Outcome == CallOutcome.Success ? fallback.Message : primary.Message;
    }

    private enum CallOutcome { Success, RateLimited, ServerError, ClientError }

    private readonly record struct ProviderCallResult(CallOutcome Outcome, string Message);

    /// <summary>Posts one OpenAI-compatible chat completion. Never throws for HTTP-level failures - returns a
    /// typed outcome plus a user-facing message so the caller can decide whether to fail over.</summary>
    private async Task<ProviderCallResult> CallProviderAsync(
        string baseUrl,
        string model,
        string apiKey,
        int rpm,
        IReadOnlyList<ChatMessage> messages,
        bool isFallback,
        CancellationToken cancellationToken)
    {
        await _throttle.AwaitThrottleAsync(rpm, cancellationToken).ConfigureAwait(false);

        var chatRequest = new ChatRequest(Model: model, Temperature: 0.2, Messages: messages);
        var uri = new Uri($"{baseUrl.TrimEnd('/')}/chat/completions");

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = JsonContent.Create(chatRequest, options: JsonOptions);

        var label = isFallback ? "fallback" : "primary";
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "RAG {Label} provider request failed to reach the endpoint.", label);
            return new ProviderCallResult(CallOutcome.ServerError,
                "The assistant provider is unreachable right now. Please try again in a moment.");
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta;
                await _throttle.RecordRateLimitAsync(retryAfter, cancellationToken).ConfigureAwait(false);
                return new ProviderCallResult(CallOutcome.RateLimited,
                    "The assistant is rate-limited right now. Please try again in a moment.");
            }

            if (!response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("RAG {Label} chat request failed: {Status} {Body}", label, (int)response.StatusCode, content);
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return new ProviderCallResult(CallOutcome.ClientError,
                        "The Library assistant API key was rejected. Check the key in **Settings → AI → Library AI assistant**.");
                if ((int)response.StatusCode >= 500)
                    return new ProviderCallResult(CallOutcome.ServerError,
                        $"The assistant provider returned a server error ({(int)response.StatusCode}). Please try again.");
                return new ProviderCallResult(CallOutcome.ClientError,
                    $"The assistant provider returned an error ({(int)response.StatusCode}). Check the model id and base URL in Settings, then try again.");
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var chatResponse = JsonSerializer.Deserialize<ChatResponse>(responseJson, JsonOptions);
            var text = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content;

            if (string.IsNullOrWhiteSpace(text))
                return new ProviderCallResult(CallOutcome.ServerError, "The assistant returned an empty response. Please try again.");

            return new ProviderCallResult(CallOutcome.Success, text);
        }
    }

    private static IReadOnlyList<ChatMessage> BuildMessages(
        string persona,
        LibraryChatRequestDto request,
        IReadOnlyList<LibraryRecommendedSeriesDto> cards)
    {
        var messages = new List<ChatMessage> { new("system", $"{persona.Trim()}\n\n{GroundingRules}") };

        if (request.History is { Count: > 0 })
        {
            foreach (var turn in request.History.TakeLast(MaxHistoryTurns))
            {
                var role = string.Equals(turn.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
                if (!string.IsNullOrWhiteSpace(turn.Content))
                    messages.Add(new ChatMessage(role, turn.Content));
            }
        }

        messages.Add(new ChatMessage("user", $"{BuildContextBlock(cards)}\n\nQuestion: {request.Message}"));
        return messages;
    }

    private static string BuildContextBlock(IReadOnlyList<LibraryRecommendedSeriesDto> cards)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Catalog context:");
        for (var i = 0; i < cards.Count; i++)
        {
            var card = cards[i];
            var genres = card.Genres.Count > 0 ? string.Join(", ", card.Genres) : "unknown";
            var synopsis = string.IsNullOrWhiteSpace(card.Synopsis) ? "(no synopsis)" : card.Synopsis!.Trim();
            sb.AppendLine($"{i + 1}. {card.Title} [{card.MediaType}] — genres: {genres}");
            sb.AppendLine($"   {synopsis}");
        }
        return sb.ToString();
    }

    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("messages")] IReadOnlyList<ChatMessage> Messages);

    private sealed record ChatMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<ChatChoice>? Choices);

    private sealed record ChatChoice(
        [property: JsonPropertyName("message")] ChatResponseMessage? Message);

    private sealed record ChatResponseMessage(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("content")] string? Content);
}
