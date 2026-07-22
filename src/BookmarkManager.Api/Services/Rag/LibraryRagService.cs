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
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.Rag;

/// <summary>Retrieval-augmented Library assistant. Embeds the user's question, pulls the nearest catalog
/// entries via <see cref="IVectorSearchService"/>, and asks an OpenAI-compatible chat model (same
/// request/response shape as <c>GroqSeriesIdentificationClient</c>) to answer grounded on that context.</summary>
public sealed class LibraryRagService : ILibraryRagService
{
    /// <summary>Named <see cref="IHttpClientFactory"/> client so the RAG LLM call gets its own pipeline.</summary>
    public const string HttpClientName = nameof(LibraryRagService);

    private const int MaxHistoryTurns = 6;
    private const string SystemInstructions =
        "You are the Library assistant for a personal manga/light-novel/web-novel catalog. Answer the "
        + "user's question using ONLY the numbered catalog entries provided as context. Recommend "
        + "specific titles from that context by name, briefly explaining why each fits. If the context "
        + "does not contain anything relevant, say so plainly instead of inventing titles. Respond in "
        + "concise Markdown.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IEmbeddingService _embeddingService;
    private readonly IVectorSearchService _vectorSearch;
    private readonly AppDbContext _db;
    private readonly AiTaggingSettingsService _settings;
    private readonly AiRequestThrottle _throttle;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LibraryRagService> _logger;

    public LibraryRagService(
        IEmbeddingService embeddingService,
        IVectorSearchService vectorSearch,
        AppDbContext db,
        AiTaggingSettingsService settings,
        AiRequestThrottle throttle,
        IHttpClientFactory httpClientFactory,
        ILogger<LibraryRagService> logger)
    {
        _embeddingService = embeddingService ?? throw new ArgumentNullException(nameof(embeddingService));
        _vectorSearch = vectorSearch ?? throw new ArgumentNullException(nameof(vectorSearch));
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

        var queryVector = await _embeddingService.EmbedAsync(request.Message, cancellationToken).ConfigureAwait(false);
        var hits = await _vectorSearch
            .SearchAsync(queryVector, EmbeddingConstants.RagTopK, EmbeddingConstants.RagMinSimilarity, cancellationToken)
            .ConfigureAwait(false);

        var cards = await LoadRecommendedSeriesAsync(hits, cancellationToken).ConfigureAwait(false);
        if (cards.Count == 0)
        {
            return new LibraryChatResponseDto(
                "I couldn't find anything in your catalog that matches that. Try rephrasing, or browse the catalog directly.",
                Array.Empty<LibraryRecommendedSeriesDto>());
        }

        var markdown = await CompleteAsync(request, cards, cancellationToken).ConfigureAwait(false);
        return new LibraryChatResponseDto(markdown, cards);
    }

    /// <summary>Loads catalog rows for the search hits, preserving the search's ranking and score.</summary>
    private async Task<IReadOnlyList<LibraryRecommendedSeriesDto>> LoadRecommendedSeriesAsync(
        IReadOnlyList<(Guid Id, float Score)> hits,
        CancellationToken cancellationToken)
    {
        if (hits.Count == 0)
            return Array.Empty<LibraryRecommendedSeriesDto>();

        var ids = hits.Select(h => h.Id).ToList();
        var entriesById = await _db.LibraryCatalogEntries
            .AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken)
            .ConfigureAwait(false);

        var cards = new List<LibraryRecommendedSeriesDto>(hits.Count);
        foreach (var (id, score) in hits)
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
                score));
        }

        return cards;
    }

    /// <summary>Builds the grounded prompt and calls the OpenAI-compatible chat endpoint.</summary>
    private async Task<string> CompleteAsync(
        LibraryChatRequestDto request,
        IReadOnlyList<LibraryRecommendedSeriesDto> cards,
        CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.RagApiKey))
            throw new InvalidOperationException("A RAG API key is required before the Library assistant can answer.");

        await _throttle.AwaitThrottleAsync(settings.RagRequestsPerMinute, cancellationToken).ConfigureAwait(false);

        var messages = BuildMessages(request, cards);
        var chatRequest = new ChatRequest(
            Model: string.IsNullOrWhiteSpace(settings.RagModel) ? "llama-3.3-70b-versatile" : settings.RagModel,
            Temperature: 0.2,
            Messages: messages);

        var baseUrl = string.IsNullOrWhiteSpace(settings.RagBaseUrl) ? "https://api.groq.com/openai/v1" : settings.RagBaseUrl;
        var uri = new Uri($"{baseUrl.TrimEnd('/')}/chat/completions");

        var httpClient = _httpClientFactory.CreateClient(HttpClientName);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.RagApiKey);
        httpRequest.Content = JsonContent.Create(chatRequest, options: JsonOptions);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta;
            await _throttle.RecordRateLimitAsync(retryAfter, cancellationToken).ConfigureAwait(false);
            return "The assistant is rate-limited right now. Please try again in a moment.";
        }

        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new HttpRequestException("RAG key/config problem. Check the Library assistant API key in Settings.", null, response.StatusCode);
            throw new HttpRequestException($"RAG chat request failed with status {(int)response.StatusCode} {response.StatusCode}: {content}", null, response.StatusCode);
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var chatResponse = JsonSerializer.Deserialize<ChatResponse>(responseJson, JsonOptions);
        var text = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("The Library assistant returned an empty response.");

        return text;
    }

    private static IReadOnlyList<ChatMessage> BuildMessages(
        LibraryChatRequestDto request,
        IReadOnlyList<LibraryRecommendedSeriesDto> cards)
    {
        var messages = new List<ChatMessage> { new("system", SystemInstructions) };

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
