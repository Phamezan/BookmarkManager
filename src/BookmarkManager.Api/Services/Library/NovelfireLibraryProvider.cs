using System.Net;
using System.Text.RegularExpressions;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Services.Library;

/// <summary>
/// Novelfire has no official API and its <c>robots.txt</c> explicitly disallows <c>/search?keyword=*</c>
/// and <c>/api/*</c> for all user agents, so <see cref="SearchAsync"/> deliberately always returns empty -
/// this provider only ever fetches the genre-listing pages (<c>/genre-all/.../all-novel?page=N</c>) and
/// individual novel pages (<c>/book/{slug}</c>), neither of which robots.txt disallows. Bulk-imported
/// entries still surface in live search once cached in <see cref="LibraryCatalogEntry"/> - see
/// <c>LibrarySearchService</c>'s catalog-merge behavior - so this is bulk-catalog-only by design, not a
/// missing feature. Fills the Webnovel gap AniList/MangaDex/RanobeDB leave empty: raw and
/// fan-translated web serials (mostly Chinese, with Korean removed site-wide for copyright reasons per
/// the site's own banner).
/// </summary>
public sealed partial class NovelfireLibraryProvider(
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    ILogger<NovelfireLibraryProvider> logger,
    IOptions<LibraryProviderOptions> options)
    : LibraryMediaProviderBase(httpFactory, cache, logger), IMediaProvider, IBulkCatalogProvider
{
    private const string BaseUrl = "https://novelfire.net";
    private const int ItemsPerPage = 24;

    private static readonly TimeSpan DetailsCacheTtl = TimeSpan.FromHours(3);
    private static readonly TimeSpan CatalogCacheTtl = TimeSpan.FromHours(6);

    private static readonly ProviderRateLimiter RateLimiter = new(
        tokenLimit: 6,
        tokensPerPeriod: 2,
        replenishmentPeriod: TimeSpan.FromSeconds(2));

    public override string ProviderName => "Novelfire";

    public bool IsEnabled => options.Value.EnableNovelfire;

    /// <summary>Always empty - see the type-level doc comment on why live search is off-limits here.</summary>
    public Task<IReadOnlyList<LibraryEntryDto>> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<LibraryEntryDto>>([]);

    public IReadOnlyList<string> CatalogMediaTypeQueries { get; } = ["genre-all"];

    public Task<CatalogPageResult> GetCatalogPageAsync(string mediaTypeQuery, string? continuationToken, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
            return Task.FromResult(new CatalogPageResult([], null));

        var page = continuationToken is not null && int.TryParse(continuationToken, out var parsedPage) ? parsedPage : 1;
        var cacheKey = $"{ProviderName}:catalog:{page}";
        var url = $"{BaseUrl}/genre-all/sort-popular/status-all/all-novel?page={page}";

        return ExecuteAsync(
            cacheKey,
            CatalogCacheTtl,
            TimeSpan.FromSeconds(15),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                var html = await GetHtmlAsync(url, ct).ConfigureAwait(false);
                var entries = html is null ? [] : ParseGenreListing(html, ProviderName);
                var next = entries.Count == 0 ? null : (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var rankBase = (page - 1) * ItemsPerPage;
                return new CatalogPageResult(entries, next, rankBase);
            },
            new CatalogPageResult([], null),
            cancellationToken);
    }

    public Task<LibraryEntryDto?> GetDetailsAsync(string providerId, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
            return Task.FromResult<LibraryEntryDto?>(null);

        var cacheKey = $"{ProviderName}:details:{providerId}";
        var url = BuildSourceUrl(providerId);

        return ExecuteAsync(
            cacheKey,
            DetailsCacheTtl,
            TimeSpan.FromSeconds(10),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                var html = await GetHtmlAsync(url, ct).ConfigureAwait(false);
                return html is null ? null : ParseNovelPage(html, providerId, ProviderName);
            },
            null,
            cancellationToken);
    }

    private async Task<string?> GetHtmlAsync(string url, CancellationToken cancellationToken)
    {
        var http = CreateClient();
        http.DefaultRequestHeaders.Clear();
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Logger.LogWarning("Novelfire returned non-success code: {Status} for {Url}", response.StatusCode, url);
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Parses a <c>/genre-all/.../all-novel?page=N</c> listing page - one <c>&lt;li class="novel-item"&gt;</c>
    /// per title, with cover/title/optional rating but no genres/author/synopsis (those only live on the
    /// per-novel detail page, see <see cref="ParseNovelPage"/>).</summary>
    public static List<LibraryEntryDto> ParseGenreListing(string html, string providerName)
    {
        var results = new List<LibraryEntryDto>();
        foreach (var cardBlock in SplitNovelItemCards(html))
        {
            var slugMatch = BookLinkRegex().Match(cardBlock);
            var titleMatch = NovelTitleRegex().Match(cardBlock);
            if (!slugMatch.Success || !titleMatch.Success)
                continue;

            var slug = slugMatch.Groups["slug"].Value;
            var title = CleanText(StripTags(titleMatch.Groups["title"].Value));
            if (slug.Length == 0 || title.Length == 0)
                continue;

            var coverMatch = DataSrcRegex().Match(cardBlock);
            var coverUrl = coverMatch.Success ? ResolveUrl(WebUtility.HtmlDecode(coverMatch.Groups["src"].Value)) : null;

            double? rating = RatingBadgeRegex().Match(cardBlock) is { Success: true } ratingMatch &&
                              double.TryParse(ratingMatch.Groups["rating"].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedRating)
                ? parsedRating
                : null;

            // The listing card only ever shows a running chapter count, not a specific latest-chapter
            // title/number - stash it in LatestChapter anyway so the card at least shows something
            // ("Ch. 3090") without waiting on the enrichment fetch that hits the detail page.
            var chapterCount = ChapterCountRegex().Match(cardBlock) is { Success: true } chapterMatch
                ? chapterMatch.Groups["count"].Value
                : null;

            results.Add(new LibraryEntryDto(
                providerName,
                slug,
                title,
                [],
                [],
                LibraryMediaType.Webnovel,
                coverUrl,
                null,
                [],
                rating,
                null,
                chapterCount,
                null,
                null,
                BuildSourceUrl(slug)));
        }

        return results;
    }

    /// <summary>Splits listing HTML into one chunk per <c>novel-item</c> card so each field regex only
    /// ever runs against a single novel's markup (mirrors <see cref="RoyalRoadLibraryProvider"/>).</summary>
    private static List<string> SplitNovelItemCards(string html)
    {
        var starts = NovelItemStartRegex().Matches(html).Select(m => m.Index).ToList();
        var cards = new List<string>();
        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i];
            var end = i + 1 < starts.Count ? starts[i + 1] : html.Length;
            cards.Add(html[start..end]);
        }

        return cards;
    }

    /// <summary>Parses a <c>/book/{slug}</c> detail page for the fields the listing page doesn't carry:
    /// author, status, genres, synopsis, and the latest-chapter/updated-ago strip.</summary>
    public static LibraryEntryDto? ParseNovelPage(string html, string providerId, string providerName)
    {
        var title = NovelH1TitleRegex().Match(html) is { Success: true } titleMatch
            ? CleanText(StripTags(titleMatch.Groups["title"].Value))
            : null;
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var coverUrl = OgPropertyRegex("og:image").Match(html) is { Success: true } imgMatch
            ? WebUtility.HtmlDecode(imgMatch.Groups["content"].Value).Trim()
            : null;

        var synopsis = ItemPropDescriptionRegex().Match(html) is { Success: true } descMatch
            ? WebUtility.HtmlDecode(descMatch.Groups["content"].Value).Trim()
            : null;

        var author = AuthorRegex().Match(html) is { Success: true } authorMatch
            ? CleanText(StripTags(authorMatch.Groups["author"].Value))
            : null;

        var status = StatusRegex().Match(html) is { Success: true } statusMatch
            ? CleanText(statusMatch.Groups["status"].Value)
            : null;

        var genres = CategoriesBlockRegex().Match(html) is { Success: true } categoriesMatch
            ? GenreAnchorRegex().Matches(categoriesMatch.Groups["block"].Value)
                .Select(m => CleanText(StripTags(m.Groups["text"].Value)))
                .Where(t => t.Length > 0)
                .ToList()
            : [];

        // Novelfire's detail page has a second, separate "Tags" section beneath Genres/Summary -
        // finer-grained tropes (e.g. "Time Skip", "Transmigration") distinct from the broad genre list.
        if (TagsBlockRegex().Match(html) is { Success: true } tagsMatch)
        {
            var tags = TagAnchorRegex().Matches(tagsMatch.Groups["block"].Value)
                .Select(m => CleanText(StripTags(m.Groups["text"].Value)))
                .Where(t => t.Length > 0);
            foreach (var tag in tags)
            {
                if (!genres.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    genres.Add(tag);
            }
        }

        string? latestChapter = null;
        DateTimeOffset? lastReleaseAt = null;
        if (ChapterLatestContainerRegex().Match(html) is { Success: true } containerMatch)
        {
            var block = containerMatch.Groups["block"].Value;
            latestChapter = LatestChapterTextRegex().Match(block) is { Success: true } chapterMatch
                ? CleanText(StripTags(chapterMatch.Groups["text"].Value))
                : null;
            lastReleaseAt = UpdatedAgoRegex().Match(block) is { Success: true } updatedMatch
                ? ParseRelativeTime(updatedMatch.Groups["num"].Value, updatedMatch.Groups["unit"].Value)
                : null;
        }

        latestChapter ??= HeaderChapterCountRegex().Match(html) is { Success: true } headerChapterMatch
            ? headerChapterMatch.Groups["count"].Value
            : null;

        return new LibraryEntryDto(
            providerName,
            providerId,
            title,
            [],
            author is { Length: > 0 } ? [author] : [],
            LibraryMediaType.Webnovel,
            coverUrl,
            synopsis,
            genres,
            null,
            status,
            latestChapter,
            null,
            lastReleaseAt,
            BuildSourceUrl(providerId));
    }

    /// <summary>Novelfire only shows a relative "Updated N units ago" string, never an absolute
    /// timestamp, so this is a best-effort approximation from the moment of the fetch - good enough to
    /// rank "recently updated" without claiming false precision.</summary>
    private static DateTimeOffset? ParseRelativeTime(string numText, string unit)
    {
        var amount = int.TryParse(numText, out var parsed) ? parsed : 1;
        var now = DateTimeOffset.UtcNow;
        return unit.ToLowerInvariant() switch
        {
            "second" => now.AddSeconds(-amount),
            "minute" => now.AddMinutes(-amount),
            "hour" => now.AddHours(-amount),
            "day" => now.AddDays(-amount),
            "week" => now.AddDays(-amount * 7),
            "month" => now.AddDays(-amount * 30),
            "year" => now.AddDays(-amount * 365),
            _ => null,
        };
    }

    private static string ResolveUrl(string url) => url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : $"{BaseUrl}{url}";

    private static string BuildSourceUrl(string slug) => $"{BaseUrl}/book/{slug}";

    private static string StripTags(string value) => Regex.Replace(value, "<.*?>", " ", RegexOptions.Singleline);

    private static string CleanText(string value) => WhitespaceRegex().Replace(WebUtility.HtmlDecode(value), " ").Trim();

    [GeneratedRegex("""(?is)<li\b[^>]*class\s*=\s*['"][^'"]*novel-item[^'"]*['"][^>]*>""")]
    private static partial Regex NovelItemStartRegex();

    [GeneratedRegex("""(?is)href\s*=\s*['"]/book/(?<slug>[^'"/?#]+)['"]""")]
    private static partial Regex BookLinkRegex();

    [GeneratedRegex("""(?is)<h4\b[^>]*class\s*=\s*['"][^'"]*novel-title[^'"]*['"][^>]*>(?<title>.*?)</h4>""")]
    private static partial Regex NovelTitleRegex();

    [GeneratedRegex("""(?is)data-src\s*=\s*['"](?<src>[^'"]+)['"]""")]
    private static partial Regex DataSrcRegex();

    [GeneratedRegex("""(?is)<span\b[^>]*class\s*=\s*['"][^'"]*_br[^'"]*['"][^>]*>.*?(?<rating>\d+(?:\.\d+)?)\s*</span>""")]
    private static partial Regex RatingBadgeRegex();

    [GeneratedRegex("""(?is)(?<count>\d+)\s*Chapters""")]
    private static partial Regex ChapterCountRegex();

    [GeneratedRegex("""(?is)<strong\b[^>]*>\s*<i\b[^>]*class\s*=\s*['"][^'"]*icon-book-open[^'"]*['"][^>]*>\s*</i>\s*(?<count>\d+)\s*</strong>\s*<small>\s*Chapters\s*</small>""")]
    private static partial Regex HeaderChapterCountRegex();

    [GeneratedRegex("""(?is)<h1\b[^>]*class\s*=\s*['"][^'"]*novel-title[^'"]*['"][^>]*>(?<title>.*?)</h1>""")]
    private static partial Regex NovelH1TitleRegex();

    [GeneratedRegex("""(?is)<meta\b[^>]*itemprop\s*=\s*['"]description['"][^>]*content\s*=\s*['"](?<content>[^'"]*)['"]""")]
    private static partial Regex ItemPropDescriptionRegex();

    [GeneratedRegex("""(?is)<span\b[^>]*itemprop\s*=\s*['"]author['"][^>]*>(?<author>.*?)</span>""")]
    private static partial Regex AuthorRegex();

    [GeneratedRegex("""(?is)<strong\b[^>]*>(?<status>[^<]+)</strong>\s*<small>\s*Status\s*</small>""")]
    private static partial Regex StatusRegex();

    [GeneratedRegex("""(?is)<div\b[^>]*class\s*=\s*['"][^'"]*categories[^'"]*['"][^>]*>.*?<ul>(?<block>.*?)</ul>""")]
    private static partial Regex CategoriesBlockRegex();

    [GeneratedRegex("""(?is)<a\b[^>]*class\s*=\s*['"][^'"]*property-item[^'"]*['"][^>]*>(?<text>.*?)</a>""")]
    private static partial Regex GenreAnchorRegex();

    [GeneratedRegex("""(?is)<div\b[^>]*class\s*=\s*['"][^'"]*\btags\b[^'"]*['"][^>]*>.*?<ul\b[^>]*class\s*=\s*['"][^'"]*content[^'"]*['"][^>]*>(?<block>.*?)</ul>""")]
    private static partial Regex TagsBlockRegex();

    [GeneratedRegex("""(?is)<a\b[^>]*class\s*=\s*['"][^'"]*\btag\b[^'"]*['"][^>]*>(?<text>.*?)</a>""")]
    private static partial Regex TagAnchorRegex();

    [GeneratedRegex("""(?is)<a\b[^>]*class\s*=\s*['"][^'"]*chapter-latest-container[^'"]*['"][^>]*>(?<block>.*?)</a>""")]
    private static partial Regex ChapterLatestContainerRegex();

    [GeneratedRegex("""(?is)<p\b[^>]*class\s*=\s*['"][^'"]*latest[^'"]*['"][^>]*>(?<text>.*?)</p>""")]
    private static partial Regex LatestChapterTextRegex();

    [GeneratedRegex("""(?is)(?:(?<num>\d+)|an?)\s*(?<unit>second|minute|hour|day|week|month|year)s?\s*ago""")]
    private static partial Regex UpdatedAgoRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private static Regex OgPropertyRegex(string property) => new(
        $"""(?is)<meta\s+property\s*=\s*['"]{Regex.Escape(property)}['"]\s+content\s*=\s*['"](?<content>[^'"]*)['"]""");
}
