namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>A bookmark as the library-sync planner sees it (soft-deleted rows included).</summary>
public sealed record SuwayomiLibrarySyncBookmark(
    Guid Id, string Title, string? Url, int? SuwayomiMangaId, int? CurrentProgress, bool IsDeleted,
    bool SuwayomiRemoved);

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

/// <summary>
/// A series to remove from the Suwayomi library because every bookmark linking it was deleted.
/// <see cref="BookmarkIds"/> are the deleted bookmarks to stamp with <c>SuwayomiRemovedAt</c> once
/// the removal succeeds.
/// </summary>
public sealed record SuwayomiLibraryRemoval(int MangaId, IReadOnlyList<Guid> BookmarkIds);

/// <summary>
/// A bookmark whose series left the library on a previous delete and is now restored. The marker is
/// cleared once the series is back in the library; <see cref="MangaInLibrary"/> is true when it is
/// already there, so only the marker needs clearing.
/// </summary>
public sealed record SuwayomiLibraryRestore(Guid BookmarkId, int MangaId, bool MangaInLibrary);

/// <summary>The per-cycle diff produced by <see cref="SuwayomiLibrarySyncPlanner"/>.</summary>
public sealed record SuwayomiLibrarySyncPlan(
    IReadOnlyList<SuwayomiLibrarySyncCreation> Creations,
    IReadOnlyList<SuwayomiLibrarySyncLink> Links,
    IReadOnlyList<SuwayomiLibrarySyncRelink> Relinks,
    IReadOnlyList<Guid> Removals,
    IReadOnlyList<SuwayomiLibrarySyncProgress> ProgressUpdates,
    IReadOnlySet<int> Unlinked,
    bool LibraryEmpty,
    int RemovalsSuppressed,
    IReadOnlyList<SuwayomiLibraryRemoval> LibraryRemovals,
    IReadOnlyList<SuwayomiLibraryRestore> LibraryRestores,
    int LibraryRemovalsSuppressed);

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
/// <item>Removes the series from the Suwayomi library when a bookmark linking it was deleted and no
/// live bookmark still links it (throttled per cycle); restores the series when a marked bookmark
/// comes back from the recycle bin.</item>
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
        int maxRemovalsPerCycle,
        int maxLibraryRemovalsPerCycle)
    {
        var baseUrl = publicBaseUrl.TrimEnd('/');
        var byId = new Dictionary<int, SuwayomiLibraryManga>();
        foreach (var manga in library)
        {
            byId.TryAdd(manga.Id, manga);
        }

        // "Linked" includes soft-deleted bookmarks: a bookmark the user deleted in BookmarkManager
        // must not be resurrected while it sits in the recycle bin.
        // ponytail: a deleted bookmark now also removes its series from the Suwayomi library
        // (LibraryRemovals below), so the row's marker outlives the 30-day purge only until the
        // series leaves the library; once purged there is nothing left to re-create, because the
        // library entry is gone too.
        var linkedIds = bookmarks
            .Where(b => b.SuwayomiMangaId is not null)
            .Select(b => b.SuwayomiMangaId!.Value)
            .ToHashSet();

        var unlinked = library.Select(m => m.Id).Where(id => !linkedIds.Contains(id)).ToHashSet();

        // A deleted, not-yet-stamped bookmark removes its series from the library unless a live
        // bookmark still links that series. Grouped by manga id and ordered so the per-cycle cap is
        // deterministic.
        var liveLinkedMangas = bookmarks
            .Where(b => !b.IsDeleted && b.SuwayomiMangaId is not null)
            .Select(b => b.SuwayomiMangaId!.Value)
            .ToHashSet();

        var libraryRemovals = new List<SuwayomiLibraryRemoval>();
        var libraryRemovalsSuppressed = 0;
        if (library.Count > 0)
        {
            var removalGroups = bookmarks
                .Where(b => b.IsDeleted
                    && !b.SuwayomiRemoved
                    && b.SuwayomiMangaId is int mangaId
                    && byId.ContainsKey(mangaId))
                .GroupBy(b => b.SuwayomiMangaId!.Value)
                .OrderBy(g => g.Key);

            foreach (var group in removalGroups)
            {
                if (liveLinkedMangas.Contains(group.Key))
                {
                    continue;
                }

                if (libraryRemovals.Count >= maxLibraryRemovalsPerCycle)
                {
                    libraryRemovalsSuppressed += group.Count();
                    continue;
                }

                libraryRemovals.Add(new SuwayomiLibraryRemoval(group.Key, group.Select(b => b.Id).ToList()));
            }
        }

        // A live, marked bookmark means its series was removed on a previous delete and the bookmark
        // has come back (recycle-bin restore): re-add the series when it is gone, then clear the
        // marker. Excluding marked bookmarks from the removal candidates below keeps this from being
        // re-recycled in the same cycle.
        var libraryRestores = bookmarks
            .Where(b => !b.IsDeleted && b.SuwayomiRemoved && b.SuwayomiMangaId is int mangaId && !byId.ContainsKey(mangaId))
            .Select(b => new SuwayomiLibraryRestore(b.Id, b.SuwayomiMangaId!.Value, MangaInLibrary: false))
            .Concat(bookmarks
                .Where(b => !b.IsDeleted && b.SuwayomiRemoved && b.SuwayomiMangaId is int mangaId && byId.ContainsKey(mangaId))
                .Select(b => new SuwayomiLibraryRestore(b.Id, b.SuwayomiMangaId!.Value, MangaInLibrary: true)))
            .ToList();

        // A bookmark whose old series left the library is a removal candidate unless it can be
        // relinked to the same series under a new id (source "Migrate"): match the bookmark's title
        // against every still-unlinked library series, and only accept an unambiguous 1:1 pair.
        var removalCandidates = bookmarks
            .Where(b => !b.IsDeleted && !b.SuwayomiRemoved && b.SuwayomiMangaId is int mangaId && !byId.ContainsKey(mangaId))
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
            suppressRemovals ? pendingRemovals.Count : 0,
            libraryRemovals,
            libraryRestores,
            libraryRemovalsSuppressed);
    }

    private static int? FloorProgress(double? chapter)
        => chapter is { } value ? (int)Math.Floor(value) : null;
}
