using System.Text.Json.Serialization;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.BookmarkTagging;

internal interface IAiSeriesIdentificationClient
{
    Task<AiProviderResponse> IdentifyAsync(
        AiSeriesIdentifyRequest request,
        CancellationToken cancellationToken);
}

internal sealed record AiProviderResponse(
    string Json,
    AiProviderRateLimit? RateLimit = null);

internal sealed record AiProviderRateLimit(
    bool IsRateLimited,
    TimeSpan? RetryAfter,
    string? Message);

internal sealed record AiSeriesIdentifyRequest(
    string Instructions,
    IReadOnlyList<AiSeriesIdentifyRequestItem> Items);

internal sealed record AiSeriesIdentifyRequestItem(
    Guid Id,
    string Title,
    [property: JsonPropertyName("url_host")] string? UrlHost,
    [property: JsonPropertyName("folder_path")] string? FolderPath,
    [property: JsonPropertyName("domain_hint")] BookmarkTagDomainDto DomainHint);
