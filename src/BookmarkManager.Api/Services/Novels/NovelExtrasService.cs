using System.Text.RegularExpressions;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Services.Novels;

public sealed partial class NovelExtrasService
{
    private const string RoyalRoadSite = "https://www.royalroad.com";
    private const string RoyalRoadRateLimiterKey = "royalroad";
    private const int MaxShortFieldLength = 256;
    private const int MaxSummaryLength = 8000;
    public const int MaxReviewTextLength = 20_000;

    private static readonly TimeSpan StatsCacheTtl = TimeSpan.FromHours(1);
    private static readonly TimeSpan ReviewsCacheTtl = TimeSpan.FromHours(6);

    private static readonly HashSet<string> SupportedExtrasSources = new(StringComparer.OrdinalIgnoreCase)
    {
        "novelfire",
        "hangulplanet"
    };

    private readonly NovelService _novels;
    private readonly INovelSidecarClient _sidecar;
    private readonly NovelOptions _options;
    private readonly IMemoryCache _cache;
    private readonly NovelRateLimiterRegistry _rateLimiters;

    public NovelExtrasService(
        NovelService novels,
        INovelSidecarClient sidecar,
        IOptions<NovelOptions> options,
        IMemoryCache cache,
        NovelRateLimiterRegistry rateLimiters)
    {
        _novels = novels;
        _sidecar = sidecar;
        _options = options.Value;
        _cache = cache;
        _rateLimiters = rateLimiters;
    }

    public bool IsRoyalRoadReviewsEnabled => _options.RoyalRoadReviews;

    public static bool SupportsExtras(string? source)
        => !string.IsNullOrWhiteSpace(source) && SupportedExtrasSources.Contains(source.Trim());

    public async Task<NovelStatsDto?> GetStatsAsync(string source, string path, CancellationToken ct)
    {
        var meta = await _novels.ResolveEnabledSourceAsync(source, ct).ConfigureAwait(false);
        if (meta is null || !SupportsExtras(meta.Id))
        {
            return null;
        }

        var cacheKey = $"novels:stats:{meta.Id}:{path}";
        if (_cache.TryGetValue(cacheKey, out NovelStatsDto? cached) && cached is not null)
        {
            return cached;
        }

        await _rateLimiters.GetForSource(meta.Id).WaitAsync(ct).ConfigureAwait(false);
        var raw = await _sidecar.GetStatsAsync(meta.Id, path, ct).ConfigureAwait(false);

        var dto = new NovelStatsDto
        {
            Source = meta.Id,
            Path = path,
            Rank = raw.Rank,
            Rating = raw.Rating,
            RatingCount = raw.RatingCount,
            Views = raw.Views,
            Bookmarks = raw.Bookmarks,
            Chapters = raw.Chapters,
            Status = TruncateOptional(raw.Status, 64),
            LatestChapterName = TruncateOptional(raw.LatestChapterName, MaxShortFieldLength),
            LatestChapterUpdated = TruncateOptional(raw.LatestChapterUpdated, 64),
            ReviewCount = raw.ReviewCount,
            ReviewAverage = raw.ReviewAverage,
            Summary = TruncateOptional(raw.Summary, MaxSummaryLength)
        };

        _cache.Set(cacheKey, dto, StatsCacheTtl);
        return dto;
    }

    public async Task<NovelReviewsPageDto?> GetReviewsAsync(string source, string path, int page, CancellationToken ct)
    {
        var meta = await _novels.ResolveEnabledSourceAsync(source, ct).ConfigureAwait(false);
        if (meta is null || !SupportsExtras(meta.Id))
        {
            return null;
        }

        var cacheKey = $"novels:reviews:{meta.Id}:{path}:{page}";
        if (_cache.TryGetValue(cacheKey, out NovelReviewsPageDto? cached) && cached is not null)
        {
            return cached;
        }

        await _rateLimiters.GetForSource(meta.Id).WaitAsync(ct).ConfigureAwait(false);
        var raw = await _sidecar.GetReviewsAsync(meta.Id, path, page, ct).ConfigureAwait(false);

        var dto = new NovelReviewsPageDto
        {
            Source = meta.Id,
            SourceName = meta.Name,
            Site = meta.Site,
            Path = path,
            Average = raw.Average,
            Count = raw.Count,
            Page = raw.Page,
            HasNextPage = raw.HasNextPage,
            Items = MapReviews(raw.Items)
        };

        _cache.Set(cacheKey, dto, ReviewsCacheTtl);
        return dto;
    }

    public async Task<RoyalRoadReviewsPageDto?> GetRoyalRoadReviewsAsync(string title, int page, CancellationToken ct)
    {
        if (!_options.RoyalRoadReviews)
        {
            return null;
        }

        var trimmed = title.Trim();
        var normalized = MediaTitleNormalizer.NormalizeForSearch(trimmed);
        if (normalized.Length == 0)
        {
            return new RoyalRoadReviewsPageDto { Matched = false, Page = page };
        }

        var matched = await ResolveRoyalRoadMatchAsync(trimmed, normalized, ct).ConfigureAwait(false);
        if (matched is null)
        {
            return new RoyalRoadReviewsPageDto { Matched = false, Page = page };
        }

        var pageCacheKey = $"novels:rr:fiction:{matched.Path}:{page}";
        if (_cache.TryGetValue(pageCacheKey, out RoyalRoadReviewsPageDto? cachedPage) && cachedPage is not null)
        {
            return cachedPage;
        }

        await _rateLimiters.GetForSource(RoyalRoadRateLimiterKey).WaitAsync(ct).ConfigureAwait(false);
        var fiction = await _sidecar.GetRoyalRoadFictionAsync(matched.Path, page, ct).ConfigureAwait(false);

        var dto = new RoyalRoadReviewsPageDto
        {
            Matched = true,
            Title = TruncateOptional(fiction.Title ?? matched.Title, NovelPathValidator.MaxTitleLength),
            Path = matched.Path,
            Site = RoyalRoadSite,
            OverallScore = fiction.OverallScore,
            Ratings = fiction.Ratings,
            Views = fiction.Views,
            Followers = fiction.Followers,
            Favorites = fiction.Favorites,
            Count = fiction.Count,
            Page = fiction.Page,
            HasNextPage = fiction.HasNextPage,
            Items = MapReviews(fiction.Items)
        };

        _cache.Set(pageCacheKey, dto, ReviewsCacheTtl);
        return dto;
    }

    private sealed record CachedRoyalRoadMatch(string? Path, string? Title);

    private async Task<CachedRoyalRoadMatch?> ResolveRoyalRoadMatchAsync(
        string rawTitle,
        string normalizedTitle,
        CancellationToken ct)
    {
        var searchCacheKey = $"novels:rr:search:{normalizedTitle}";
        if (_cache.TryGetValue(searchCacheKey, out CachedRoyalRoadMatch? cached) && cached is not null)
        {
            return cached.Path is null ? null : cached;
        }

        await _rateLimiters.GetForSource(RoyalRoadRateLimiterKey).WaitAsync(ct).ConfigureAwait(false);
        var hits = await _sidecar.SearchRoyalRoadAsync(rawTitle, ct).ConfigureAwait(false);

        var exact = hits.FirstOrDefault(h =>
            !string.IsNullOrWhiteSpace(h.Path)
            && RoyalRoadPathRegex().IsMatch(h.Path)
            && string.Equals(
                MediaTitleNormalizer.NormalizeForSearch(h.Title),
                normalizedTitle,
                StringComparison.Ordinal));

        var entry = exact is null
            ? new CachedRoyalRoadMatch(null, null)
            : new CachedRoyalRoadMatch(exact.Path, exact.Title);

        _cache.Set(searchCacheKey, entry, ReviewsCacheTtl);
        return entry.Path is null ? null : entry;
    }

    internal static List<NovelReviewItemDto> MapReviews(IReadOnlyList<SidecarReviewItemRecord>? items)
    {
        if (items is null || items.Count == 0)
        {
            return [];
        }

        var result = new List<NovelReviewItemDto>(items.Count);
        foreach (var item in items)
        {
            var paragraphs = TruncateParagraphs(item.Paragraphs);
            if (paragraphs.Count == 0)
            {
                continue;
            }

            result.Add(new NovelReviewItemDto
            {
                Author = TruncateOptional(item.Author, 128) ?? "Anonymous",
                Date = TruncateOptional(item.Date, 64),
                Score = item.Score,
                Likes = item.Likes,
                Paragraphs = paragraphs
            });
        }

        return result;
    }

    private static List<NovelReviewParagraphDto> TruncateParagraphs(IReadOnlyList<SidecarReviewParagraphRecord>? raw)
    {
        if (raw is null || raw.Count == 0)
        {
            return [];
        }

        var paragraphs = new List<NovelReviewParagraphDto>(raw.Count);
        var remaining = MaxReviewTextLength;

        foreach (var p in raw)
        {
            if (remaining <= 0)
            {
                break;
            }

            var cleaned = CleanPlainText(p.Text);
            if (cleaned.Length == 0)
            {
                continue;
            }

            if (cleaned.Length > remaining)
            {
                cleaned = cleaned[..remaining];
            }

            remaining -= cleaned.Length;
            paragraphs.Add(new NovelReviewParagraphDto
            {
                Text = cleaned,
                Spoiler = p.Spoiler
            });
        }

        return paragraphs;
    }

    private static string? TruncateOptional(string? value, int maxLength)
    {
        var cleaned = CleanPlainText(value);
        if (cleaned.Length == 0)
        {
            return null;
        }
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static string CleanPlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Replace("\0", string.Empty, StringComparison.Ordinal).Trim();
    }

    [GeneratedRegex(@"^/fiction/\d+/[a-z0-9-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex RoyalRoadPathRegex();
}
