using System;
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
    private readonly ILogger<CompositeSeriesIdentificationClient> _logger;

    public CompositeSeriesIdentificationClient(
        OpenRouterSeriesIdentificationClient primary,
        GroqSeriesIdentificationClient fallback,
        AiTaggingSettingsService settings,
        ILogger<CompositeSeriesIdentificationClient> logger)
    {
        _primary = primary;
        _fallback = fallback;
        _settings = settings;
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
        => string.Equals(request.Provider, "Groq", StringComparison.OrdinalIgnoreCase)
            ? _fallback.TestConnectionAsync(request, cancellationToken)
            : _primary.TestConnectionAsync(request, cancellationToken);
}
