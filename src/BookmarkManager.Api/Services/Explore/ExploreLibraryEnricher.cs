using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.Suwayomi;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services.Explore;

/// <summary>
/// What Explore knows about the user: the Suwayomi library (by AniList id, and by normalized title as
/// a fallback) and the saved / dismissed list.
/// </summary>
public sealed record ExploreUserState(
    IReadOnlyDictionary<int, DiscoverProgressValue> LibraryByAnilistId,
    IReadOnlyDictionary<string, DiscoverProgressValue> LibraryByTitle,
    IReadOnlyDictionary<int, string> ListStates);

/// <summary>
/// Adds library progress and saved/dismissed state to Explore items. The library link is by AniList id
/// through <see cref="SeriesMetadata"/> rows, falling back to the normalized english / romaji /
/// synonym titles looked up in the Discover progress index.
/// </summary>
public sealed class ExploreLibraryEnricher
{
    private readonly AppDbContext _db;
    private readonly DiscoverLibraryProgressProvider _progress;

    public ExploreLibraryEnricher(AppDbContext db, DiscoverLibraryProgressProvider progress)
    {
        _db = db;
        _progress = progress;
    }

    public async Task<ExploreUserState> LoadStateAsync(CancellationToken ct)
    {
        var byTitle = await _progress.GetAsync(ct).ConfigureAwait(false);

        var rows = await _db.SeriesMetadataSet
            .AsNoTracking()
            .Where(m => m.Found && m.AnilistId != null)
            .Select(m => new { m.NormalizedKey, AnilistId = m.AnilistId!.Value })
            .ToListAsync(ct).ConfigureAwait(false);

        var byId = new Dictionary<int, DiscoverProgressValue>();
        foreach (var row in rows)
        {
            if (byTitle.TryGetValue(row.NormalizedKey, out var value))
            {
                byId.TryAdd(row.AnilistId, value);
            }
        }

        var list = await _db.ExploreListEntries
            .AsNoTracking()
            .ToDictionaryAsync(e => e.AnilistId, e => e.State, ct).ConfigureAwait(false);

        return new ExploreUserState(byId, byTitle, list);
    }

    /// <summary>Sets <c>Library</c> and <c>ListState</c> on every item (overwriting both).</summary>
    public static void Apply(IEnumerable<ExploreItemDto> items, ExploreUserState state)
    {
        foreach (var item in items)
        {
            item.Library = FindLibrary(item, state) is { } v
                ? new ExploreLibraryDto { MangaId = v.MangaId, Read = v.Read, Latest = v.Latest, SourceName = v.SourceName }
                : null;
            item.ListState = state.ListStates.GetValueOrDefault(item.AnilistId);
        }
    }

    /// <summary>Enriches, then drops dismissed / library items as the filter asks. Mutates the items.</summary>
    public static List<ExploreItemDto> Prepare(IEnumerable<ExploreItemDto> items, ExploreFilter filter, ExploreUserState state)
    {
        var list = items.ToList();
        Apply(list, state);
        return list
            .Where(i => !(filter.HideDismissed && i.ListState == ExploreListEntry.Dismissed))
            .Where(i => !(filter.HideLibrary && i.Library is not null))
            .ToList();
    }

    public static bool IsInLibrary(ExploreItemDto item, ExploreUserState state) => FindLibrary(item, state) is not null;

    private static DiscoverProgressValue? FindLibrary(ExploreItemDto item, ExploreUserState state)
    {
        if (state.LibraryByAnilistId.TryGetValue(item.AnilistId, out var byId))
        {
            return byId;
        }

        var titles = new[] { item.Title, item.RomajiTitle }.Concat(item.Synonyms);
        foreach (var title in titles)
        {
            var key = string.IsNullOrWhiteSpace(title) ? string.Empty : MediaTitleNormalizer.NormalizeForSearch(title);
            if (key.Length > 0 && state.LibraryByTitle.TryGetValue(key, out var byTitle))
            {
                return byTitle;
            }
        }

        return null;
    }
}
