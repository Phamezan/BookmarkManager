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

internal sealed class OpenRouterSeriesIdentificationClient : IAiSeriesIdentificationClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AiTaggingSettingsService _settings;
    private readonly AiRequestThrottle _throttle;
    private readonly ILogger<OpenRouterSeriesIdentificationClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public OpenRouterSeriesIdentificationClient(
        IHttpClientFactory httpClientFactory,
        AiTaggingSettingsService settings,
        AiRequestThrottle throttle,
        ILogger<OpenRouterSeriesIdentificationClient> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _throttle = throttle ?? throw new ArgumentNullException(nameof(throttle));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<AiProviderResponse> IdentifyAsync(
        AiSeriesIdentifyRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.Enabled)
        {
            throw new InvalidOperationException("OpenRouter AI auto-tagging is disabled in Settings.");
        }

        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException("OpenRouter API key is required before AI auto-tagging can identify series titles.");
        }

        await _throttle.AwaitThrottleAsync(settings.RequestsPerMinute, cancellationToken).ConfigureAwait(false);

        var itemsJson = JsonSerializer.Serialize(request.Items, JsonOptions);

        var openRouterRequest = new OpenRouterChatRequest(
            Model: string.IsNullOrWhiteSpace(settings.Model) ? "google/gemini-2.5-flash:free" : settings.Model,
            Temperature: 0.0,
            Messages: new[]
            {
                new OpenRouterMessage("system", request.Instructions),
                new OpenRouterMessage("user", itemsJson)
            });

        var baseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl) ? "https://openrouter.ai/api/v1" : settings.BaseUrl;
        var uri = new Uri($"{baseUrl.TrimEnd('/')}/chat/completions");

        var httpClient = _httpClientFactory.CreateClient(nameof(OpenRouterSeriesIdentificationClient));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, uri);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        httpRequest.Content = JsonContent.Create(openRouterRequest, options: JsonOptions);

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
                new AiProviderRateLimit(true, retryAfter ?? TimeSpan.FromSeconds(60), "OpenRouter rate limit reached."));
        }

        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.PaymentRequired)
            {
                throw new HttpRequestException("OpenRouter credit/balance problem. Check your OpenRouter credits.", null, response.StatusCode);
            }
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                throw new HttpRequestException("OpenRouter key/config problem. Check your OpenRouter settings and API Key.", null, response.StatusCode);
            }
            throw new HttpRequestException($"OpenRouter API request failed with status {(int)response.StatusCode} {response.StatusCode}: {content}", null, response.StatusCode);
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var chatResponse = JsonSerializer.Deserialize<OpenRouterChatResponse>(responseJson, JsonOptions);
        var text = chatResponse?.Choices?.FirstOrDefault()?.Message?.Content;

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("OpenRouter returned an empty AI identification response.");
        }

        return new AiProviderResponse(text);
    }

    private sealed record OpenRouterChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("messages")] IReadOnlyList<OpenRouterMessage> Messages);

    private sealed record OpenRouterMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content);

    private sealed record OpenRouterChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<OpenRouterChoice>? Choices);

    private sealed record OpenRouterChoice(
        [property: JsonPropertyName("message")] OpenRouterResponseMessage? Message);

    private sealed record OpenRouterResponseMessage(
        [property: JsonPropertyName("role")] string? Role,
        [property: JsonPropertyName("content")] string? Content);
}
