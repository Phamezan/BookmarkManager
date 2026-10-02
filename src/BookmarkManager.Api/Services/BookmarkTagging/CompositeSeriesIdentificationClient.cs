using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.BookmarkTagging;

// OpenRouter is primary (broadest free model selection). When its free-tier quota is exhausted,
// OpenRouter answers with a rate-limit response rather than throwing - that's the signal this
// falls back to Groq on, for this chunk only. Config errors (missing/disabled key) still throw
// straight through; only a real rate-limit response triggers the fallback.
internal sealed class CompositeSeriesIdentificationClient : IAiSeriesIdentificationClient
{
    private readonly OpenRouterSeriesIdentificationClient _primary;
    private readonly GroqSeriesIdentificationClient _fallback;
    private readonly AiTaggingSettingsService _settings;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<CompositeSeriesIdentificationClient> _logger;

    public CompositeSeriesIdentificationClient(
        OpenRouterSeriesIdentificationClient primary,
        GroqSeriesIdentificationClient fallback,
        AiTaggingSettingsService settings,
        IHttpClientFactory httpClientFactory,
        ILogger<CompositeSeriesIdentificationClient> logger)
    {
        _primary = primary;
        _fallback = fallback;
        _settings = settings;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<AiProviderResponse> IdentifyAsync(AiSeriesIdentifyRequest request, CancellationToken cancellationToken)
    {
        var response = await _primary.IdentifyAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.RateLimit is not { IsRateLimited: true })
            return response;

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.GroqApiKey))
            return response;

        _logger.LogInformation("OpenRouter rate-limited; falling back to Groq for this chunk.");
        try
        {
            return await _fallback.IdentifyAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Groq fallback request failed; returning original OpenRouter rate limit.");
            return response;
        }
    }

    public Task<TestAiKeyResponse> TestConnectionAsync(TestAiKeyRequest request, CancellationToken cancellationToken)
    {
        // Gemini is not OpenAI-compatible: it authenticates with x-goog-api-key and does not expose
        // /chat/completions. Route it to its own probe instead of the OpenRouter client, which would
        // otherwise 404 on the stored Gemini endpoint.
        if (string.Equals(request.Provider, "Gemini", StringComparison.OrdinalIgnoreCase))
            return TestGeminiAsync(request, cancellationToken);

        // Tavily is also not OpenAI-compatible: it has its own fixed search endpoint and Bearer auth.
        if (string.Equals(request.Provider, "Tavily", StringComparison.OrdinalIgnoreCase))
            return TestTavilyAsync(request, cancellationToken);

        return string.Equals(request.Provider, "Groq", StringComparison.OrdinalIgnoreCase)
            ? _fallback.TestConnectionAsync(request, cancellationToken)
            : _primary.TestConnectionAsync(request, cancellationToken);
    }

    private async Task<TestAiKeyResponse> TestTavilyAsync(TestAiKeyRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            return new TestAiKeyResponse { Success = false, StatusCode = 0, Message = "API key is empty." };

        // The destination is the fixed API constant, never the caller-supplied BaseUrl: a stored key
        // must not be testable against an attacker-chosen host. The probe is a minimal basic search
        // (1 credit).
        var body = new
        {
            query = "ping",
            search_depth = "basic",
            max_results = 1
        };

        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, UrlMigration.TavilySearchService.ApiEndpoint);
            httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", request.ApiKey);
            httpRequest.Content = JsonContent.Create(body);

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
                return new TestAiKeyResponse { Success = true, StatusCode = status, Message = "OK - Tavily key accepted (1 search credit used)." };

            if (response.StatusCode == HttpStatusCode.TooManyRequests
                || (int)response.StatusCode is 432 or 433)
            {
                return new TestAiKeyResponse
                {
                    Success = true,
                    StatusCode = status,
                    Message = "Key is valid, but Tavily is rate-limiting or out of credits right now."
                };
            }

            var failureBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var reason = UrlMigration.TavilyApiError.Describe(response.StatusCode, failureBody);

            var hint = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Key rejected (invalid or unauthorized).",
                _ => "Tavily request failed."
            };

            return new TestAiKeyResponse { Success = false, StatusCode = status, Message = $"{hint} {reason}." };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Tavily key test request failed to reach the provider.");
            return new TestAiKeyResponse { Success = false, StatusCode = 0, Message = $"Could not reach provider: {ex.Message}" };
        }
    }

    private async Task<TestAiKeyResponse> TestGeminiAsync(TestAiKeyRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ApiKey))
            return new TestAiKeyResponse { Success = false, StatusCode = 0, Message = "API key is empty." };

        var baseUrl = string.IsNullOrWhiteSpace(request.BaseUrl)
            ? "https://generativelanguage.googleapis.com/v1beta"
            : request.BaseUrl.TrimEnd('/');
        var uri = new Uri($"{baseUrl}/models");

        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            using var httpRequest = new HttpRequestMessage(HttpMethod.Get, uri);
            httpRequest.Headers.TryAddWithoutValidation("x-goog-api-key", request.ApiKey);

            using var response = await httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
                return new TestAiKeyResponse { Success = true, StatusCode = status, Message = "OK - Gemini key accepted." };

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new TestAiKeyResponse { Success = true, StatusCode = status, Message = "Key is valid, but Gemini is rate-limiting right now." };

            // Include Google's own status/message (sanitized) so a 402/403 explains itself instead
            // of only showing the numeric code. Non-JSON bodies fall back to the status code.
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var reason = GeminiApiError.Describe(response.StatusCode, body);

            var hint = response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                    "Key rejected (invalid or not authorized for the Generative Language API).",
                HttpStatusCode.NotFound =>
                    "Endpoint not found - check the Gemini endpoint/base URL in Settings.",
                _ => "Gemini request failed."
            };

            return new TestAiKeyResponse { Success = false, StatusCode = status, Message = $"{hint} {reason}." };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Gemini key test request failed to reach the provider.");
            return new TestAiKeyResponse { Success = false, StatusCode = 0, Message = $"Could not reach provider: {ex.Message}" };
        }
    }
}
