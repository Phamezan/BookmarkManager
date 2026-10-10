namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>A bookmark as the library-sync planner sees it (soft-deleted rows included).</summary>
public sealed record SuwayomiLibrarySyncBookmark(
    Guid Id, string Title, string? Url, int? SuwayomiMangaId, int? CurrentProgress, bool IsDeleted);

/// <summary>A library series to create a new bookmark for.</summary>
public sealed record SuwayomiLibrarySyncCreation(
    int MangaId, string Title, string Url, string? SourceUrl, int? CurrentProgress);

/// <summary>A trial-era bookmark (no <c>SuwayomiMangaId</c>) to link to a library series by URL.</summary>
public sealed record SuwayomiLibrarySyncLink(Guid BookmarkId, int MangaId, string? SourceUrl);

/// <summary>
/// A bookmark whose old series left the library but whose series reappeared under a new Suwayomi
/// id (an in-app source "Migrate"). Repoints it instead of delete+create so tags/notes/history
/// survive.
/// </summary>
public sealed record SuwayomiLibrarySyncRelink(Guid BookmarkId, int MangaId, string? SourceUrl);

/// <summary>A forward-only progress bump for a linked or relinked bookmark.</summary>
public sealed record SuwayomiLibrarySyncProgress(Guid BookmarkId, int Progress);

/// <summary>The per-cycle diff produced by <see cref="SuwayomiLibrarySyncPlanner"/>.</summary>
public sealed record SuwayomiLibrarySyncPlan(
    IReadOnlyList<SuwayomiLibrarySyncCreation> Creations,
    IReadOnlyList<SuwayomiLibrarySyncLink> Links,
    IReadOnlyList<SuwayomiLibrarySyncRelink> Relinks,
    IReadOnlyList<Guid> Removals,
    IReadOnlyList<SuwayomiLibrarySyncProgress> ProgressUpdates,
    IReadOnlySet<int> Unlinked,
    bool LibraryEmpty,
    int RemovalsSuppressed);

/// <summary>
/// Pure diff of the Suwayomi library against BookmarkManager's linked bookmarks. The service fetches
/// and applies; every guard lives here so it is unit-testable without a database:
/// <list type="bullet">
/// <item>Creates/link a series only after it was unlinked on the previous cycle (two consecutive
/// polls), so the <c>UrlMigrationApprovalService</c> writing the library link then the bookmark link
/// is never raced. The first cycle after startup creates nothing.</item>
/// <item>Links an existing bookmark with a matching page URL instead of creating a duplicate.</item>
/// <item>Relinks a bookmark whose series moved to a new Suwayomi id (in-app Migrate) when the
/// title match is unambiguous, so the bookmark is not deleted and re-created.</item>
/// <item>Removes linked bookmarks for series no longer in the library, but never when the library is
/// empty or the batch exceeds the per-cycle cap (relinks never count toward that cap).</item>
/// <item>Raises progress only, never lowers it.</item>
/// </list>
/// </summary>
public static class SuwayomiLibrarySyncPlanner
{
    public static SuwayomiLibrarySyncPlan Plan(
        IReadOnlyList<SuwayomiLibraryManga> library,
        IReadOnlyList<SuwayomiLibrarySyncBookmark> bookmarks,
        IReadOnlySet<int> previouslyUnlinked,
        bool syncFolderAvailable,
        string publicBaseUrl,
        int maxRemovalsPerCycle)
    {
        var baseUrl = publicBaseUrl.TrimEnd('/');
        var byId = new Dictionary<int, SuwayomiLibraryManga>();
        foreach (var manga in library)
        {
            byId.TryAdd(manga.Id, manga);
        }

        // "Linked" includes soft-deleted bookmarks: a bookmark the user deleted in BookmarkManager
        // must not be resurrected while it sits in the recycle bin.
        // ponytail: after 30-day purge the row is gone, so the series would be re-created. The
        // upgrade path is BookmarkManager -> Suwayomi removal, which this planner does not do.
        var linkedIds = bookmarks
            .Where(b => b.SuwayomiMangaId is not null)
            .Select(b => b.SuwayomiMangaId!.Value)
            .ToHashSet();

        var unlinked = library.Select(m => m.Id).Where(id => !linkedIds.Contains(id)).ToHashSet();

        // A bookmark whose old series left the library is a removal candidate unless it can be
        // relinked to the same series under a new id (source "Migrate"): match the bookmark's title
        // against every still-unlinked library series, and only accept an unambiguous 1:1 pair.
        var removalCandidates = bookmarks
            .Where(b => !b.IsDeleted && b.SuwayomiMangaId is int mangaId && !byId.ContainsKey(mangaId))
            .ToList();
        var unlinkedMangas = byId.Values.Where(m => !linkedIds.Contains(m.Id)).ToList();

        var relinks = new List<SuwayomiLibrarySyncRelink>();
        var relinkedBookmarkIds = new HashSet<Guid>();
        var relinkedMangaIds = new HashSet<int>();
        var relinkProgress = new List<SuwayomiLibrarySyncProgress>();
        foreach (var bookmark in removalCandidates)
        {
            var matches = unlinkedMangas
                .Where(m => SuwayomiMatchScorer.IsExact(bookmark.Title, m.Title))
                .ToList();
            if (matches.Count != 1)
            {
                continue;
            }

            var manga = matches[0];
            if (relinkedMangaIds.Contains(manga.Id))
            {
                continue;
            }

            var contenders = removalCandidates.Count(b => SuwayomiMatchScorer.IsExact(b.Title, manga.Title));
            if (contenders != 1)
            {
                continue;
            }

            relinks.Add(new SuwayomiLibrarySyncRelink(bookmark.Id, manga.Id, manga.RealUrl));
            relinkedBookmarkIds.Add(bookmark.Id);
            relinkedMangaIds.Add(manga.Id);

            if (FloorProgress(manga.LatestReadChapter) is int relinkTarget
                && (bookmark.CurrentProgress is null || relinkTarget > bookmark.CurrentProgress))
            {
                relinkProgress.Add(new SuwayomiLibrarySyncProgress(bookmark.Id, relinkTarget));
            }
        }

        var creations = new List<SuwayomiLibrarySyncCreation>();
        var links = new List<SuwayomiLibrarySyncLink>();
        foreach (var manga in byId.Values)
        {
            if (linkedIds.Contains(manga.Id)
                || relinkedMangaIds.Contains(manga.Id)
                || !previouslyUnlinked.Contains(manga.Id))
            {
                continue;
            }

            var pageUrl = $"{baseUrl}/manga/{manga.Id}";
            var matches = bookmarks
                .Where(b => !b.IsDeleted
                    && b.SuwayomiMangaId is null
                    && string.Equals(b.Url, pageUrl, StringComparison.Ordinal))
                .ToList();

            if (matches.Count > 0)
            {
                foreach (var bookmark in matches)
                {
                    links.Add(new SuwayomiLibrarySyncLink(bookmark.Id, manga.Id, manga.RealUrl));
                }

                continue;
            }

            if (!syncFolderAvailable)
            {
                continue;
            }

            creations.Add(new SuwayomiLibrarySyncCreation(
                manga.Id, manga.Title, pageUrl, manga.RealUrl, FloorProgress(manga.LatestReadChapter)));
        }

        var pendingRemovals = removalCandidates
            .Where(b => !relinkedBookmarkIds.Contains(b.Id))
            .ToList();
        var suppressRemovals = library.Count == 0 || pendingRemovals.Count > maxRemovalsPerCycle;
        var removals = suppressRemovals ? [] : pendingRemovals.Select(b => b.Id).ToList();

        var progress = new List<SuwayomiLibrarySyncProgress>(relinkProgress);
        foreach (var bookmark in bookmarks)
        {
            if (bookmark.IsDeleted
                || bookmark.SuwayomiMangaId is not int mangaId
                || !byId.TryGetValue(mangaId, out var manga))
            {
                continue;
            }

            if (FloorProgress(manga.LatestReadChapter) is int target
                && (bookmark.CurrentProgress is null || target > bookmark.CurrentProgress))
            {
                progress.Add(new SuwayomiLibrarySyncProgress(bookmark.Id, target));
            }
        }

        return new SuwayomiLibrarySyncPlan(
            creations,
            links,
            relinks,
            removals,
            progress,
            unlinked,
            library.Count == 0,
            suppressRemovals ? pendingRemovals.Count : 0);
    }

    private static int? FloorProgress(double? chapter)
        => chapter is { } value ? (int)Math.Floor(value) : null;
}
