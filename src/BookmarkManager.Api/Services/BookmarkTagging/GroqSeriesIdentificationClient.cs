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
using BookmarkManager.Contracts;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.BookmarkTagging;

// Groq's chat completions API is OpenAI-compatible - same request/response shape as OpenRouter,
// just a different base URL, key, and model. Used as a fallback when OpenRouter's free-tier daily
// quota (not RPM) is exhausted; see CompositeSeriesIdentificationClient.
internal sealed class GroqSeriesIdentificationClient : IAiSeriesIdentificationClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AiTaggingSettingsService _settings;
    private readonly AiRequestThrottle _throttle = new();
    private readonly ILogger<GroqSeriesIdentificationClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public GroqSeriesIdentificationClient(
        IHttpClientFactory httpClientFactory,
        AiTaggingSettingsService settings,
        ILogger<GroqSeriesIdentificationClient> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AiProviderResponse> IdentifyAsync(
        AiSeriesIdentifyRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.GroqApiKey))
        {
            throw new InvalidOperationException("Groq API key is required before it can be used as a fallback provider.");
        }

        await _throttle.AwaitThrottleAsync(settings.GroqRequestsPerMinute, cancellationToken).ConfigureAwait(false);

        var itemsJson = JsonSerializer.Serialize(request.Items, JsonOptions);

        var groqRequest = new GroqChatRequest(
            Model: string.IsNullOrWhiteSpace(settings.GroqModel) ? "llama-3.3-70b-versatile" : settings.GroqModel,
            Temperature: 0.0,
            Messages: new[]
            {
                new GroqMessage("system", request.Instructions),
                new GroqMessage("user", itemsJson)
            });

        var baseUrl = string.IsNullOrWhiteSpace(settings.GroqBaseUrl) ? "https://api.groq.com/openai/v1" : settings.GroqBaseUrl;
        var uri = new Uri($"{baseUrl.TrimEnd('/')}/chat/completions");

        var httpClient = _httpClientFactory.CreateClient(nameof(GroqSeriesIdentificationClient));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.GroqApiKey);
        httpRequest.Content = JsonContent.Create(groqRequest, options: JsonOptions);

        using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            TimeSpan? retryAfter = null;
            if (response.Headers.RetryAfter != null)
            {
                if (response.Headers.RetryAfter.Delta.HasValue)
                {
                    retryAfter = response.Headers.RetryAfter.Delta.Value;
                }
                else if (response.Headers.RetryAfter.Date.HasValue)
                {
                    retryAfter = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                }
            }

            await _throttle.RecordRateLimitAsync(retryAfter, cancellationToken).ConfigureAwait(false);

            return new AiProviderResponse(
                string.Empty,
                new AiProviderRateLimit(true, retryAfter ?? TimeSpan.FromSeconds(60), "Groq rate limit reached."));
        }

        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new HttpRequestException("Groq key/config problem. Check your Groq settings and API Key.", null, response.StatusCode);
            }
            throw new HttpRequestException($"Groq API request failed with status {(int)response.StatusCode} {response.StatusCode}: {content}", null, response.StatusCode);
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var chatResponse = JsonSerializer.Deserialize<GroqChatResponse>(responseJson, JsonOptions);
        var text = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("Groq returned an empty AI identification response.");
        }

        return new AiProviderResponse(text);
    }

    public async Task<TestAiKeyResponse> TestConnectionAsync(TestAiKeyRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            return new TestAiKeyResponse { Success = false, StatusCode = 0, Message = "API key is empty." };
        if (string.IsNullOrWhiteSpace(request.Model))
            return new TestAiKeyResponse { Success = false, StatusCode = 0, Message = "Model is empty." };

        var baseUrl = string.IsNullOrWhiteSpace(request.BaseUrl) ? "https://api.groq.com/openai/v1" : request.BaseUrl;
        var uri = new Uri($"{baseUrl.TrimEnd('/')}/chat/completions");

        var body = new GroqChatRequest(
            Model: request.Model,
            Temperature: 0.0,
            Messages: new[] { new GroqMessage("user", "ping") });

        try
        {
            var httpClient = _httpClientFactory.CreateClient(nameof(GroqSeriesIdentificationClient));
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ApiKey);
            httpRequest.Content = JsonContent.Create(body, options: JsonOptions);

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return new TestAiKeyResponse { Success = true, StatusCode = status, Message = $"OK - key accepted and model '{request.Model}' responded." };

            var providerMessage = ExtractProviderError(content);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                var note = string.IsNullOrWhiteSpace(providerMessage) ? "" : $" ({providerMessage})";
                return new TestAiKeyResponse
                {
                    Success = true,
                    StatusCode = status,
                    Message = $"Key is valid and '{request.Model}' resolved, but you hit Groq's rate limit.{note}"
                };
            }

            var hint = response.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "Key rejected (invalid or revoked). Generate a new key at console.groq.com/keys.",
                System.Net.HttpStatusCode.Forbidden => "Key forbidden for this model or endpoint.",
                System.Net.HttpStatusCode.NotFound => $"Model '{request.Model}' not found - check the id (e.g. 'llama-3.3-70b-versatile').",
                System.Net.HttpStatusCode.BadRequest => $"Bad request - often an invalid model id ('{request.Model}').",
                _ => "Request failed."
            };

            var message = string.IsNullOrWhiteSpace(providerMessage) ? hint : $"{hint} ({providerMessage})";
            return new TestAiKeyResponse { Success = false, StatusCode = status, Message = message };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Groq key test request failed to reach the provider.");
            return new TestAiKeyResponse { Success = false, StatusCode = 0, Message = $"Could not reach provider: {ex.Message}" };
        }
    }

    private static string? ExtractProviderError(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var msg))
                    return msg.GetString();
                if (error.ValueKind == JsonValueKind.String)
                    return error.GetString();
            }
        }
        catch (JsonException)
        {
            // Non-JSON error body - fall through to a trimmed raw snippet.
        }
        return content.Length > 200 ? content[..200] : content;
    }

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
