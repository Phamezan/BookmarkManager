using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.Library;

public sealed record LibraryReleaseInfo(
    string? LatestChapter,
    string? LatestVolume,
    DateTimeOffset? LastReleaseAt,
    string? SourceUrl);

public interface IMediaProvider
{
    /// <summary>Stable provider name, matches <see cref="LibraryEntryDto.Provider"/> and the HttpClient registration name.</summary>
    string ProviderName { get; }

    /// <summary>False when the provider is gated behind a feature flag (e.g. NovelUpdates) and currently off.</summary>
    bool IsEnabled { get; }

    Task<IReadOnlyList<LibraryEntryDto>> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken);

    Task<LibraryEntryDto?> GetDetailsAsync(string providerId, CancellationToken cancellationToken);

    Task<LibraryReleaseInfo?> GetLatestReleaseAsync(string providerId, CancellationToken cancellationToken);
}
