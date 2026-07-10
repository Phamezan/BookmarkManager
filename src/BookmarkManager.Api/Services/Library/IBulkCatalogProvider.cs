using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.Library;

/// <summary>One page of a bulk-import crawl. <see cref="NextContinuationToken"/> is null when the
/// provider has no more results for this sequence (natural exhaustion). <see cref="RankBase"/>, when
/// present, is the popularity rank of the first entry in <see cref="Entries"/> (subsequent entries take
/// consecutive ranks); null means this page carries no popularity signal (e.g. a coverage-only crawl).</summary>
public sealed record CatalogPageResult(
    IReadOnlyList<LibraryEntryDto> Entries,
    string? NextContinuationToken,
    int? RankBase = null);

/// <summary>Implemented by providers whose public API supports true full-catalog pagination (i.e. can
/// walk every entry, not just a capped "top N"). Used only by <see cref="LibraryCatalogSyncBackgroundService"/>
/// for the background bulk import/refresh crawl that backs the Library "Browse" view - never called from
/// a live user request, so it deliberately bypasses the small page caps <see cref="IMediaProvider.SearchAsync"/>
/// and <see cref="ITrendingMediaProvider.GetTrendingAsync"/> use to keep interactive calls fast.</summary>
public interface IBulkCatalogProvider : IMediaProvider
{
    /// <summary>Independent crawl sequences this provider supports; each is seeded/resumed/chained
    /// separately as its own durable queue sequence.</summary>
    IReadOnlyList<string> CatalogMediaTypeQueries { get; }

    Task<CatalogPageResult> GetCatalogPageAsync(
        string mediaTypeQuery,
        string? continuationToken,
        CancellationToken cancellationToken);
}
