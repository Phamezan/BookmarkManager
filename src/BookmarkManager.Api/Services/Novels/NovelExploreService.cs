using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Services.Novels;

public enum NovelListUpdateOutcome
{
    Ok,
    NotFound,
    ValidationError
}

public sealed record NovelListUpdateResult(NovelListUpdateOutcome Outcome, string? ErrorMessage = null);

/// <summary>
/// Powers Explore Novels v2 endpoints: cached in-memory catalog querying, meta genre counts,
/// shuffled trendy recommends, shelves, For-you recommendations, and Saved/Dismissed list state.
/// </summary>
public sealed class NovelExploreService
{
    private const string CatalogCacheKey = "novels:catalog:rows";
    private const int TrendyPoolLimit = 300;
    private const double TrendyMinRating = 3.5;
    private const int ShelfLimit = 18;
    private const double CompletedGemsMinRating = 4.0;
    private const double CompletedGemsMinPopularityScore = 0.2;

    private static readonly TimeSpan CatalogCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RecommendsPoolTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ShelvesCacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan ForYouCacheTtl = TimeSpan.FromHours(1);

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly NovelOptions _options;
    private readonly NovelForYouScorer _forYou;

    public NovelExploreService(
        AppDbContext db,
        IMemoryCache cache,
        IOptions<NovelOptions> options,
        NovelForYouScorer forYou)
    {
        _db = db;
        _cache = cache;
        _options = options.Value;
        _forYou = forYou;
    }

    private bool IsNovelfireEnabled => _options.GetEnabledSourceSet().Contains("novelfire");

    public void InvalidateUserCaches()
    {
        foreach (var readable in new[] { true, false })
        {
            _cache.Remove(RecommendsPoolKey(readable));
            _cache.Remove(ShelvesKey(readable));
            _cache.Remove(ForYouKey(readable));
        }
    }

    public void InvalidateAllCaches()
    {
        _cache.Remove(CatalogCacheKey);
        InvalidateUserCaches();
    }

    public async Task<NovelCatalogPageDto> GetCatalogAsync(NovelCatalogFilter filter, CancellationToken ct)
    {
        var rows = await GetCatalogRowsAsync(ct).ConfigureAwait(false);
        var (libraryTitles, listStates) = await LoadUserStateAsync(ct).ConfigureAwait(false);
        var novelfireEnabled = IsNovelfireEnabled;

        var filtered = NovelCatalogEngine.Filter(rows, filter, novelfireEnabled, libraryTitles, listStates);
        var sorted = NovelCatalogEngine.Sort(filtered, filter.Sort).ToList();

        var total = sorted.Count;
        var items = sorted
            .Skip((filter.Page - 1) * NovelCatalogFilter.PageSize)
            .Take(NovelCatalogFilter.PageSize)
            .Select(r => NovelCatalogEngine.ToDto(r, novelfireEnabled, libraryTitles, listStates))
            .ToList();

        return new NovelCatalogPageDto
        {
            Page = filter.Page,
            HasNextPage = filter.Page * NovelCatalogFilter.PageSize < total,
            Total = total,
            Items = items
        };
    }

    public async Task<NovelMetaDto> GetMetaAsync(CancellationToken ct)
    {
        var rows = await GetCatalogRowsAsync(ct).ConfigureAwait(false);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var canonicalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            foreach (var genre in row.GenreSet)
            {
                counts[genre] = counts.GetValueOrDefault(genre) + 1;
                canonicalNames.TryAdd(genre, genre);
            }
        }

        var genres = counts
            .Select(kv => new NovelGenreCountDto { Name = canonicalNames[kv.Key], Count = kv.Value })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new NovelMetaDto { Genres = genres };
    }

    public async Task<List<NovelCatalogItemDto>> GetRecommendsAsync(int count, bool readable, CancellationToken ct)
    {
        var clampedCount = Math.Clamp(count, 1, 24);
        var poolKey = RecommendsPoolKey(readable);
        var novelfireEnabled = IsNovelfireEnabled;

        if (!_cache.TryGetValue(poolKey, out List<NovelCatalogRow>? pool) || pool is null)
        {
            var rows = await GetCatalogRowsAsync(ct).ConfigureAwait(false);
            var (libraryTitles, listStates) = await LoadUserStateAsync(ct).ConfigureAwait(false);

            pool = rows
                .Where(r =>
                    (!readable || r.IsReadable(novelfireEnabled))
                    && (r.Rating is null || r.Rating >= TrendyMinRating)
                    && !NovelCatalogEngine.IsInLibrary(r, libraryTitles)
                    && !IsDismissed(r.Id, listStates))
                .OrderBy(r => r.PopularityScore)
                .ThenBy(r => r.PopularityRank is null ? 1 : 0)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .Take(TrendyPoolLimit)
                .ToList();

            _cache.Set(poolKey, pool, RecommendsPoolTtl);
        }

        if (pool.Count == 0)
        {
            return [];
        }

        var picked = SampleWithoutReplacement(pool, clampedCount);
        var (currentLib, currentList) = await LoadUserStateAsync(ct).ConfigureAwait(false);
        return picked
            .Select(r => NovelCatalogEngine.ToDto(r, novelfireEnabled, currentLib, currentList))
            .ToList();
    }

    public async Task<List<NovelShelfDto>> GetShelvesAsync(bool readable, CancellationToken ct)
    {
        var cacheKey = ShelvesKey(readable);
        if (_cache.TryGetValue(cacheKey, out List<NovelShelfDto>? cached) && cached is not null)
        {
            return cached;
        }

        var rows = await GetCatalogRowsAsync(ct).ConfigureAwait(false);
        var (libraryTitles, listStates) = await LoadUserStateAsync(ct).ConfigureAwait(false);
        var novelfireEnabled = IsNovelfireEnabled;

        var eligible = rows
            .Where(r =>
                (!readable || r.IsReadable(novelfireEnabled))
                && !NovelCatalogEngine.IsInLibrary(r, libraryTitles)
                && !IsDismissed(r.Id, listStates))
            .ToList();

        NovelCatalogItemDto Map(NovelCatalogRow row, IReadOnlyList<string>? becauseOf = null) =>
            NovelCatalogEngine.ToDto(row, novelfireEnabled, libraryTitles, listStates, becauseOf);

        var shelves = new List<NovelShelfDto>();

        var forYouRanked = await GetForYouRankedAsync(readable, ct).ConfigureAwait(false);
        if (forYouRanked.Count > 0)
        {
            shelves.Add(new NovelShelfDto
            {
                Key = "for-you",
                Title = "For you",
                Subtitle = "Based on your novel library and bookmarks",
                BrowseQuery = "view=for-you",
                Items = forYouRanked.Take(ShelfLimit).Select(r => Map(r.Row, r.BecauseOf)).ToList()
            });
        }

        shelves.Add(new NovelShelfDto
        {
            Key = "trending",
            Title = "Trending",
            Subtitle = "Most popular across the catalog",
            BrowseQuery = "sort=popular",
            Items = eligible
                .OrderBy(r => r.PopularityScore)
                .ThenBy(r => r.PopularityRank is null ? 1 : 0)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .Take(ShelfLimit)
                .Select(r => Map(r))
                .ToList()
        });

        shelves.Add(new NovelShelfDto
        {
            Key = "top-rated",
            Title = "Top rated",
            Subtitle = "Highest reader ratings",
            BrowseQuery = "sort=rating",
            Items = eligible
                .Where(r => r.Rating is not null)
                .OrderByDescending(r => r.Rating)
                .ThenBy(r => r.PopularityScore)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .Take(ShelfLimit)
                .Select(r => Map(r))
                .ToList()
        });

        shelves.Add(new NovelShelfDto
        {
            Key = "updated",
            Title = "Recently updated",
            Subtitle = "Latest chapter releases",
            BrowseQuery = "sort=updated",
            Items = eligible
                .Where(r => r.LastReleaseAt is not null)
                .OrderByDescending(r => r.LastReleaseAt)
                .ThenBy(r => r.PopularityScore)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .Take(ShelfLimit)
                .Select(r => Map(r))
                .ToList()
        });

        shelves.Add(new NovelShelfDto
        {
            Key = "completed-gems",
            Title = "Completed gems",
            Subtitle = "Finished, highly rated, off the beaten path",
            BrowseQuery = "status=Completed&minRating=4&sort=rating",
            Items = eligible
                .Where(r =>
                    NovelCatalogFilter.IsCompletedStatus(r.Status)
                    && r.Rating is >= CompletedGemsMinRating
                    && r.PopularityScore >= CompletedGemsMinPopularityScore)
                .OrderByDescending(r => r.Rating)
                .ThenBy(r => r.PopularityScore)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .Take(ShelfLimit)
                .Select(r => Map(r))
                .ToList()
        });

        _cache.Set(cacheKey, shelves, ShelvesCacheTtl);
        return shelves;
    }

    public async Task<NovelCatalogPageDto> GetForYouPageAsync(int page, bool readable, CancellationToken ct)
    {
        var pageNumber = Math.Max(page, 1);
        var ranked = await GetForYouRankedAsync(readable, ct).ConfigureAwait(false);
        var (libraryTitles, listStates) = await LoadUserStateAsync(ct).ConfigureAwait(false);
        var novelfireEnabled = IsNovelfireEnabled;

        var items = ranked
            .Skip((pageNumber - 1) * NovelCatalogFilter.PageSize)
            .Take(NovelCatalogFilter.PageSize)
            .Select(r => NovelCatalogEngine.ToDto(r.Row, novelfireEnabled, libraryTitles, listStates, r.BecauseOf))
            .ToList();

        return new NovelCatalogPageDto
        {
            Page = pageNumber,
            HasNextPage = pageNumber * NovelCatalogFilter.PageSize < ranked.Count,
            Total = ranked.Count,
            Items = items
        };
    }

    public async Task<List<NovelCatalogItemDto>> GetListAsync(string state, CancellationToken ct)
    {
        var entries = await _db.NovelListEntries
            .AsNoTracking()
            .Where(e => e.State == state)
            .OrderByDescending(e => e.UpdatedAt)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (entries.Count == 0)
        {
            return [];
        }

        var rows = await GetCatalogRowsAsync(ct).ConfigureAwait(false);
        var byId = rows.ToDictionary(r => r.Id);
        var (libraryTitles, listStates) = await LoadUserStateAsync(ct).ConfigureAwait(false);
        var novelfireEnabled = IsNovelfireEnabled;

        var items = new List<NovelCatalogItemDto>(entries.Count);
        foreach (var entry in entries)
        {
            if (byId.TryGetValue(entry.CatalogEntryId, out var row))
            {
                items.Add(NovelCatalogEngine.ToDto(row, novelfireEnabled, libraryTitles, listStates));
            }
            else
            {
                items.Add(new NovelCatalogItemDto
                {
                    Id = entry.CatalogEntryId,
                    Title = entry.Title,
                    CoverUrl = entry.CoverUrl,
                    ListState = entry.State
                });
            }
        }

        return items;
    }

    public async Task<NovelListUpdateResult> PutListAsync(
        Guid catalogEntryId,
        NovelListUpdateRequest? request,
        CancellationToken ct)
    {
        if (request is null)
        {
            return new NovelListUpdateResult(NovelListUpdateOutcome.ValidationError, "Request body is required.");
        }

        var requestedState = request.State?.Trim().ToLowerInvariant();
        if (requestedState is not null && requestedState is not (NovelListEntry.Saved or NovelListEntry.Dismissed))
        {
            return new NovelListUpdateResult(
                NovelListUpdateOutcome.ValidationError,
                "state must be 'saved', 'dismissed' or null.");
        }

        if (request.Title is { Length: > NovelPathValidator.MaxTitleLength })
        {
            return new NovelListUpdateResult(
                NovelListUpdateOutcome.ValidationError,
                $"title must be at most {NovelPathValidator.MaxTitleLength} characters.");
        }

        var coverError = NovelPathValidator.ValidateOptionalHttpUrl(request.CoverUrl, "coverUrl");
        if (coverError is not null)
        {
            return new NovelListUpdateResult(NovelListUpdateOutcome.ValidationError, coverError);
        }

        var catalogRow = await _db.LibraryCatalogEntries
            .AsNoTracking()
            .Where(e => e.Id == catalogEntryId
                        && (e.MediaType == LibraryMediaType.LightNovel || e.MediaType == LibraryMediaType.Webnovel))
            .Select(e => new { e.Id, e.Title, e.CoverImageUrl })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (catalogRow is null)
        {
            return new NovelListUpdateResult(NovelListUpdateOutcome.NotFound, "Novel catalog entry not found.");
        }

        var existing = await _db.NovelListEntries
            .FirstOrDefaultAsync(e => e.CatalogEntryId == catalogEntryId, ct)
            .ConfigureAwait(false);

        if (requestedState is null)
        {
            if (existing is not null)
            {
                _db.NovelListEntries.Remove(existing);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                InvalidateUserCaches();
            }

            return new NovelListUpdateResult(NovelListUpdateOutcome.Ok);
        }

        var resolvedTitle = !string.IsNullOrWhiteSpace(request.Title)
            ? request.Title.Trim()
            : catalogRow.Title;
        if (resolvedTitle.Length > NovelPathValidator.MaxTitleLength)
        {
            resolvedTitle = resolvedTitle[..NovelPathValidator.MaxTitleLength];
        }

        var resolvedCover = string.IsNullOrWhiteSpace(request.CoverUrl)
            ? null
            : request.CoverUrl.Trim();

        if (existing is null)
        {
            existing = new NovelListEntry { CatalogEntryId = catalogEntryId };
            _db.NovelListEntries.Add(existing);
        }

        existing.State = requestedState;
        existing.Title = resolvedTitle;
        existing.CoverUrl = resolvedCover;
        existing.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        InvalidateUserCaches();
        return new NovelListUpdateResult(NovelListUpdateOutcome.Ok);
    }

    private async Task<IReadOnlyList<NovelCatalogRow>> GetCatalogRowsAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(CatalogCacheKey, out IReadOnlyList<NovelCatalogRow>? cached) && cached is not null)
        {
            return cached;
        }

        var entries = await _db.LibraryCatalogEntries
            .AsNoTracking()
            .Where(e => e.MediaType == LibraryMediaType.LightNovel || e.MediaType == LibraryMediaType.Webnovel)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        IReadOnlyList<NovelCatalogRow> rows = NovelCatalogEngine.BuildRows(entries);
        _cache.Set(CatalogCacheKey, rows, CatalogCacheTtl);
        return rows;
    }

    private async Task<List<ScoredNovelRecommendation>> GetForYouRankedAsync(bool readable, CancellationToken ct)
    {
        var cacheKey = ForYouKey(readable);
        if (_cache.TryGetValue(cacheKey, out List<ScoredNovelRecommendation>? cached) && cached is not null)
        {
            return cached;
        }

        var rows = await GetCatalogRowsAsync(ct).ConfigureAwait(false);
        var (libraryTitles, listStates) = await LoadUserStateAsync(ct).ConfigureAwait(false);
        var ranked = await _forYou
            .RankAsync(rows, readable, IsNovelfireEnabled, libraryTitles, listStates, ct)
            .ConfigureAwait(false);

        _cache.Set(cacheKey, ranked, ForYouCacheTtl);
        return ranked;
    }

    private async Task<(HashSet<string> LibraryTitles, Dictionary<Guid, string> ListStates)> LoadUserStateAsync(
        CancellationToken ct)
    {
        var libraryTitles = await _db.NovelLibraryEntries
            .AsNoTracking()
            .Select(e => e.Title)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var normalizedLib = libraryTitles
            .Select(MediaTitleNormalizer.NormalizeForSearch)
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var listEntries = await _db.NovelListEntries
            .AsNoTracking()
            .Select(e => new { e.CatalogEntryId, e.State })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var listStates = new Dictionary<Guid, string>(listEntries.Count);
        foreach (var entry in listEntries)
        {
            listStates[entry.CatalogEntryId] = entry.State;
        }

        return (normalizedLib, listStates);
    }

    private static bool IsDismissed(Guid id, IReadOnlyDictionary<Guid, string> listStates) =>
        listStates.TryGetValue(id, out var state)
        && string.Equals(state, NovelListEntry.Dismissed, StringComparison.Ordinal);

    private static List<NovelCatalogRow> SampleWithoutReplacement(IReadOnlyList<NovelCatalogRow> pool, int count)
    {
        var take = Math.Min(count, pool.Count);
        var copy = pool.ToArray();
        for (var i = 0; i < take; i++)
        {
            var swapIndex = Random.Shared.Next(i, copy.Length);
            (copy[i], copy[swapIndex]) = (copy[swapIndex], copy[i]);
        }

        return copy[..take].ToList();
    }

    private static string RecommendsPoolKey(bool readable) => $"novels:recommends:pool:{readable}";
    private static string ShelvesKey(bool readable) => $"novels:shelves:{readable}";
    private static string ForYouKey(bool readable) => $"novels:foryou:{readable}";
}
