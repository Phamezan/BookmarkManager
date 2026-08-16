using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services;

public interface IBookmarkStatusProjectionService
{
    /// <summary>
    /// Resolves the category root folder (e.g. "Manga", "Anime", "Novel") for a given bookmark node.
    /// Returns null if the bookmark does not belong to a supported media category.
    /// </summary>
    Task<BookmarkNode?> ResolveCategoryRootAsync(BookmarkNode node, CancellationToken ct);

    /// <summary>
    /// Checks whether the bookmark belongs to a supported media category (Anime, Manga, Novel, or equivalents).
    /// </summary>
    bool IsSupportedCategory(string? category, BookmarkNode? categoryRoot, string? url, string title);

    /// <summary>
    /// Resolves or lazily creates the destination folder for the specified status under the category root.
    /// For Ongoing, returns the category root itself.
    /// For Plan to Read, Completed, and Dropped, returns the child status folder under the category root.
    /// </summary>
    Task<(BookmarkNode Folder, bool IsNewlyCreated)> ResolveTargetFolderAsync(BookmarkNode categoryRoot, string targetStatus, CancellationToken ct);

    /// <summary>
    /// Authoritatively updates a bookmark's personal status and projects its folder position,
    /// atomically persisting metadata, projection, and extension commands in one transaction.
    /// </summary>
    Task<BookmarkNode> UpdateStatusAsync(Guid bookmarkId, string targetStatus, CancellationToken ct);

    /// <summary>
    /// Bulk updates the personal status on multiple bookmarks, returning detailed per-item outcomes.
    /// Validates each item individually without failing the entire batch.
    /// </summary>
    Task<BulkUpdateBookmarkStatusResponse> BulkUpdateStatusAsync(IReadOnlyList<Guid> bookmarkIds, string targetStatus, CancellationToken ct);

    /// <summary>
    /// Discovers candidate bookmarks in the same category that match the source bookmark's series title,
    /// excluding bookmarks that already have the target status.
    /// </summary>
    Task<RelatedSeriesPreviewResponse> GetRelatedStatusCandidatesAsync(Guid bookmarkId, string targetStatus, CancellationToken ct);
}
