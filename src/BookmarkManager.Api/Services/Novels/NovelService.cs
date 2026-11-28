using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Services.Novels;

public enum ChapterLookupOutcome
{
    Ok,
    SourceDisabled,
    NotFound,
    Locked
}

public sealed record ChapterLookupResult(ChapterLookupOutcome Outcome, NovelChapterContentDto? Content = null);

public sealed class NovelService
{
    private static readonly TimeSpan SourcesCacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan PopularSearchCacheTtl = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan NovelDetailCacheTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan ChapterHtmlCacheTtl = TimeSpan.FromMinutes(30);

    private readonly AppDbContext _db;
    private readonly INovelSidecarClient _sidecar;
    private readonly NovelOptions _options;
    private readonly IMemoryCache _cache;
    private readonly NovelRateLimiterRegistry _rateLimiters;
    private readonly NovelHtmlSanitizer _sanitizer;
    private readonly ILogger<NovelService> _logger;

    public NovelService(
        AppDbContext db,
        INovelSidecarClient sidecar,
        IOptions<NovelOptions> options,
        IMemoryCache cache,
        NovelRateLimiterRegistry rateLimiters,
        NovelHtmlSanitizer sanitizer,
        ILogger<NovelService> logger)
    {
        _db = db;
        _sidecar = sidecar;
        _options = options.Value;
        _cache = cache;
        _rateLimiters = rateLimiters;
        _sanitizer = sanitizer;
        _logger = logger;
    }

    public bool IsSourceEnabled(string? source)
        => !string.IsNullOrWhiteSpace(source)
           && _options.GetEnabledSourceSet().Contains(source.Trim().ToLowerInvariant());

    public async Task<IReadOnlyList<NovelSourceDto>> GetEnabledSourcesAsync(CancellationToken ct)
    {
        var enabled = _options.GetEnabledSourceSet();
        if (enabled.Count == 0)
        {
            return [];
        }

        var all = await GetAllSidecarSourcesAsync(ct).ConfigureAwait(false);
        return all
            .Where(s => enabled.Contains(s.Id))
            .Select(s => new NovelSourceDto { Id = s.Id, Name = s.Name, Site = s.Site })
            .ToList();
    }

    public async Task<NovelSourceDto?> ResolveEnabledSourceAsync(string? source, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var normalized = source.Trim().ToLowerInvariant();
        if (!_options.GetEnabledSourceSet().Contains(normalized))
        {
            return null;
        }

        var sources = await GetEnabledSourcesAsync(ct).ConfigureAwait(false);
        return sources.FirstOrDefault(s => s.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<NovelSummaryDto>?> GetPopularAsync(string source, int page, CancellationToken ct)
    {
        var meta = await ResolveEnabledSourceAsync(source, ct).ConfigureAwait(false);
        if (meta is null)
        {
            return null;
        }

        var cacheKey = $"novels:popular:{meta.Id}:{page}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<NovelSummaryDto>? cached) && cached is not null)
        {
            return cached;
        }

        await _rateLimiters.GetForSource(meta.Id).WaitAsync(ct).ConfigureAwait(false);
        var raw = await _sidecar.GetPopularAsync(meta.Id, page, ct).ConfigureAwait(false);
        var mapped = raw.Select(r => new NovelSummaryDto
        {
            Source = meta.Id,
            SourceName = meta.Name,
            Title = r.Title,
            Path = r.Path,
            Cover = r.Cover
        }).ToList();

        _cache.Set(cacheKey, (IReadOnlyList<NovelSummaryDto>)mapped, PopularSearchCacheTtl);
        return mapped;
    }

    public async Task<IReadOnlyList<NovelSummaryDto>?> SearchAsync(string query, string? source, CancellationToken ct)
    {
        var trimmed = query.Trim();
        IReadOnlyList<NovelSourceDto> targets;

        if (!string.IsNullOrWhiteSpace(source))
        {
            var single = await ResolveEnabledSourceAsync(source, ct).ConfigureAwait(false);
            if (single is null)
            {
                return null;
            }
            targets = [single];
        }
        else
        {
            targets = await GetEnabledSourcesAsync(ct).ConfigureAwait(false);
            if (targets.Count == 0)
            {
                return [];
            }
        }

        var normalizedQuery = MediaTitleNormalizer.NormalizeForSearch(trimmed);
        var results = new List<NovelSummaryDto>();
        var failures = 0;

        foreach (var target in targets)
        {
            try
            {
                var sourceHits = await SearchSingleSourceAsync(target, trimmed, ct).ConfigureAwait(false);
                foreach (var hit in sourceHits)
                {
                    var exact = normalizedQuery.Length > 0
                        && string.Equals(
                            MediaTitleNormalizer.NormalizeForSearch(hit.Title),
                            normalizedQuery,
                            StringComparison.Ordinal);

                    results.Add(new NovelSummaryDto
                    {
                        Source = target.Id,
                        SourceName = target.Name,
                        Title = hit.Title,
                        Path = hit.Path,
                        Cover = hit.Cover,
                        IsExactMatch = exact
                    });
                }
            }
            catch (NovelSourceUnavailableException ex)
            {
                failures++;
                if (targets.Count == 1)
                {
                    throw;
                }
                _logger.LogWarning(ex, "Novel search failed for source {Source}", target.Id);
            }
        }

        if (failures == targets.Count && targets.Count > 0)
        {
            throw new NovelSourceUnavailableException("Novel source unavailable");
        }

        return results
            .OrderByDescending(r => r.IsExactMatch)
            .ToList();
    }

    public async Task<NovelDetailDto?> GetNovelAsync(string source, string path, CancellationToken ct)
    {
        var meta = await ResolveEnabledSourceAsync(source, ct).ConfigureAwait(false);
        if (meta is null)
        {
            return null;
        }

        var detail = await GetCachedSidecarNovelAsync(meta.Id, path, ct).ConfigureAwait(false);

        var entry = await _db.NovelLibraryEntries
            .FirstOrDefaultAsync(e => e.Source == meta.Id && e.NovelPath == path, ct)
            .ConfigureAwait(false);

        if (entry is not null && detail.Chapters.Count > 0 && entry.ChapterCount != detail.Chapters.Count)
        {
            entry.ChapterCount = detail.Chapters.Count;
            entry.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return new NovelDetailDto
        {
            Source = meta.Id,
            SourceName = meta.Name,
            Site = meta.Site,
            Title = detail.Title,
            Path = detail.Path,
            Cover = detail.Cover,
            Author = detail.Author,
            Status = detail.Status,
            Genres = detail.Genres,
            Summary = detail.Summary,
            Chapters = detail.Chapters.Select(c => new NovelChapterSummaryDto
            {
                Name = c.Name,
                Path = c.Path,
                Number = c.Number,
                ReleaseTime = c.ReleaseTime,
                Locked = c.Locked
            }).ToList(),
            LibraryEntry = entry is null ? null : MapLibraryEntry(entry)
        };
    }

    public async Task<ChapterLookupResult> GetChapterAsync(string source, string novelPath, string chapterPath, CancellationToken ct)
    {
        var meta = await ResolveEnabledSourceAsync(source, ct).ConfigureAwait(false);
        if (meta is null)
        {
            return new ChapterLookupResult(ChapterLookupOutcome.SourceDisabled);
        }

        var detail = await GetCachedSidecarNovelAsync(meta.Id, novelPath, ct).ConfigureAwait(false);
        var chapters = detail.Chapters;
        var index = chapters.FindIndex(c => string.Equals(c.Path, chapterPath, StringComparison.Ordinal));
        if (index < 0)
        {
            return new ChapterLookupResult(ChapterLookupOutcome.NotFound);
        }

        var chapter = chapters[index];
        if (chapter.Locked)
        {
            return new ChapterLookupResult(ChapterLookupOutcome.Locked);
        }

        var sanitizedHtml = await GetCachedSanitizedChapterHtmlAsync(meta.Id, chapterPath, ct).ConfigureAwait(false);
        var prevPath = index > 0 ? chapters[index - 1].Path : null;
        var next = index + 1 < chapters.Count ? chapters[index + 1] : null;

        return new ChapterLookupResult(
            ChapterLookupOutcome.Ok,
            new NovelChapterContentDto
            {
                Title = chapter.Name,
                Html = sanitizedHtml,
                PrevPath = prevPath,
                NextPath = next?.Path,
                NextLocked = next?.Locked ?? false,
                Site = meta.Site,
                Index = index,
                Count = chapters.Count
            });
    }

    public async Task<NovelCatalogPageDto> GetCatalogAsync(string? q, string? status, int page, CancellationToken ct)
    {
        const int pageSize = 30;
        var pageNumber = Math.Max(page, 1);

        var query = _db.LibraryCatalogEntries
            .AsNoTracking()
            .Where(e => e.MediaType == LibraryMediaType.LightNovel || e.MediaType == LibraryMediaType.Webnovel);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().ToLowerInvariant();
            query = query.Where(e =>
                e.Title.ToLower().Contains(term)
                || (e.AlternateTitles != null && e.AlternateTitles.ToLower().Contains(term)));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var statusMatches = ExpandStatusSynonyms(status.Trim());
            query = query.Where(e => e.Status != null && statusMatches.Contains(e.Status.ToLower()));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var rows = await query
            .OrderBy(e => e.PopularityRank == null ? 1 : 0)
            .ThenBy(e => e.PopularityRank)
            .ThenBy(e => e.Title)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var libraryTitles = await _db.NovelLibraryEntries
            .AsNoTracking()
            .Select(e => e.Title)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var libraryNormalized = libraryTitles
            .Select(MediaTitleNormalizer.NormalizeForSearch)
            .Where(t => t.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        var items = rows.Select(e =>
        {
            var alts = LibraryCatalogEntry.SplitList(e.AlternateTitles);
            var inLib = IsTitleInLibrary(e.Title, alts, libraryNormalized);
            return new NovelCatalogItemDto
            {
                Id = e.Id,
                Provider = e.Provider,
                Title = e.Title,
                AlternateTitles = alts,
                Authors = LibraryCatalogEntry.SplitList(e.Authors),
                MediaType = e.MediaType.ToString(),
                CoverUrl = e.CoverImageUrl,
                Synopsis = e.Synopsis,
                Genres = LibraryCatalogEntry.SplitList(e.Genres),
                Rating = e.Rating,
                Status = e.Status,
                LatestChapter = e.LatestChapter,
                LatestVolume = e.LatestVolume,
                SourceUrl = e.SourceUrl,
                PopularityRank = e.PopularityRank,
                InLibrary = inLib
            };
        }).ToList();

        return new NovelCatalogPageDto
        {
            Page = pageNumber,
            HasNextPage = pageNumber * pageSize < total,
            Total = total,
            Items = items
        };
    }

    public static NovelLibraryEntryDto MapLibraryEntry(NovelLibraryEntry entry) => new()
    {
        Id = entry.Id,
        Source = entry.Source,
        NovelPath = entry.NovelPath,
        Title = entry.Title,
        CoverUrl = entry.CoverUrl,
        Status = entry.Status,
        LastChapterPath = entry.LastChapterPath,
        LastChapterName = entry.LastChapterName,
        LastChapterIndex = entry.LastChapterIndex,
        ChapterCount = entry.ChapterCount,
        ScrollPercent = entry.ScrollPercent,
        AddedAt = entry.AddedAt,
        UpdatedAt = entry.UpdatedAt,
        LastReadAt = entry.LastReadAt
    };

    private static bool IsTitleInLibrary(string title, IReadOnlyList<string> alternates, HashSet<string> libraryNormalized)
    {
        if (libraryNormalized.Count == 0)
        {
            return false;
        }

        var normalizedTitle = MediaTitleNormalizer.NormalizeForSearch(title);
        if (normalizedTitle.Length > 0 && libraryNormalized.Contains(normalizedTitle))
        {
            return true;
        }

        foreach (var alt in alternates)
        {
            var normAlt = MediaTitleNormalizer.NormalizeForSearch(alt);
            if (normAlt.Length > 0 && libraryNormalized.Contains(normAlt))
            {
                return true;
            }
        }

        return false;
    }

    private static List<string> ExpandStatusSynonyms(string status)
    {
        var lower = status.ToLowerInvariant();
        return lower switch
        {
            "ongoing" or "releasing" => ["ongoing", "releasing"],
            "completed" or "finished" => ["completed", "finished"],
            "hiatus" or "on hiatus" => ["hiatus", "on hiatus"],
            "cancelled" or "canceled" or "dropped" => ["cancelled", "canceled", "dropped"],
            _ => [lower]
        };
    }

    private async Task<IReadOnlyList<SidecarSourceRecord>> GetAllSidecarSourcesAsync(CancellationToken ct)
    {
        const string cacheKey = "novels:sources";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<SidecarSourceRecord>? cached) && cached is not null)
        {
            return cached;
        }

        var sources = await _sidecar.GetSourcesAsync(ct).ConfigureAwait(false);
        _cache.Set(cacheKey, sources, SourcesCacheTtl);
        return sources;
    }

    private async Task<IReadOnlyList<SidecarNovelSummaryRecord>> SearchSingleSourceAsync(
        NovelSourceDto source,
        string query,
        CancellationToken ct)
    {
        var cacheKey = $"novels:search:{source.Id}:{query.ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<SidecarNovelSummaryRecord>? cached) && cached is not null)
        {
            return cached;
        }

        await _rateLimiters.GetForSource(source.Id).WaitAsync(ct).ConfigureAwait(false);
        var hits = await _sidecar.SearchAsync(source.Id, query, 1, ct).ConfigureAwait(false);
        _cache.Set(cacheKey, hits, PopularSearchCacheTtl);
        return hits;
    }

    private async Task<SidecarNovelDetailRecord> GetCachedSidecarNovelAsync(string source, string path, CancellationToken ct)
    {
        var cacheKey = $"novels:novel:{source}:{path}";
        if (_cache.TryGetValue(cacheKey, out SidecarNovelDetailRecord? cached) && cached is not null)
        {
            return cached;
        }

        await _rateLimiters.GetForSource(source).WaitAsync(ct).ConfigureAwait(false);
        var detail = await _sidecar.GetNovelAsync(source, path, ct).ConfigureAwait(false);
        _cache.Set(cacheKey, detail, NovelDetailCacheTtl);
        return detail;
    }

    private async Task<string> GetCachedSanitizedChapterHtmlAsync(string source, string path, CancellationToken ct)
    {
        var cacheKey = $"novels:chapter:{source}:{path}";
        if (_cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
        {
            return cached;
        }

        await _rateLimiters.GetForSource(source).WaitAsync(ct).ConfigureAwait(false);
        var rawHtml = await _sidecar.GetChapterHtmlAsync(source, path, ct).ConfigureAwait(false);
        var sanitized = _sanitizer.Sanitize(rawHtml);
        _cache.Set(cacheKey, sanitized, ChapterHtmlCacheTtl);
        return sanitized;
    }
}
