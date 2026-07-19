using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.Library;

/// <summary>Optional capability for providers that can list trending/popular titles without a
/// search query (AniList trending, MangaDex popular). Not every <see cref="IMediaProvider"/>
/// implements this - RoyalRoad/Kitsu don't have a comparable public endpoint.</summary>
public interface ITrendingMediaProvider
{
    Task<IReadOnlyList<LibraryEntryDto>> GetTrendingAsync(LibraryMediaType? mediaType, CancellationToken cancellationToken);
}
