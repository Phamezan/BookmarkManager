using System.Text.Json.Serialization;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public interface IAiSeriesIdentificationClient
{
    Task<AiProviderResponse> IdentifyAsync(
        AiSeriesIdentifyRequest request,
        CancellationToken cancellationToken);

    // Verify a specific baseUrl/model/apiKey with a minimal live request, without touching
    // saved settings - lets the Settings page validate credentials before persisting them.
    Task<TestAiKeyResponse> TestConnectionAsync(
        TestAiKeyRequest request,
        CancellationToken cancellationToken);
}

public sealed record AiProviderResponse(
    string Json,
    AiProviderRateLimit? RateLimit = null);

public sealed record AiProviderRateLimit(
    bool IsRateLimited,
    TimeSpan? RetryAfter,
    string? Message);

public sealed record AiSeriesIdentifyRequest(
    string Instructions,
    IReadOnlyList<AiSeriesIdentifyRequestItem> Items);

public sealed record AiSeriesIdentifyRequestItem(
    Guid Id,
    string Title,
    [property: JsonPropertyName("url_host")] string? UrlHost,
    [property: JsonPropertyName("folder_path")] string? FolderPath,
    [property: JsonPropertyName("domain_hint")] BookmarkTagDomainDto DomainHint);
