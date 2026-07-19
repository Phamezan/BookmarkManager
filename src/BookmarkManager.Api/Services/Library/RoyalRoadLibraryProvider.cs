using System.Net;
using System.Text.RegularExpressions;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Services.Library;

/// <summary>
/// RoyalRoad has no official API. This scrapes the public search page and fiction pages.
/// Documented as fragile: RoyalRoad can change markup at any time, so every parse step
/// degrades to an empty/null result on mismatch rather than throwing - a broken selector
/// here must never break the rest of a fan-out search.
/// </summary>
public sealed partial class RoyalRoadLibraryProvider(
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    ILogger<RoyalRoadLibraryProvider> logger,
    IOptions<LibraryProviderOptions> options)
    : LibraryMediaProviderBase(httpFactory, cache, logger), IMediaProvider
{
    private const string BaseUrl = "https://www.royalroad.com";
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DetailsCacheTtl = TimeSpan.FromHours(3);

    private static readonly ProviderRateLimiter RateLimiter = new(
        tokenLimit: 3,
        tokensPerPeriod: 1,
        replenishmentPeriod: TimeSpan.FromSeconds(2));

    public override string ProviderName => "RoyalRoad";

    public bool IsEnabled => options.Value.EnableRoyalRoad;

    public Task<IReadOnlyList<LibraryEntryDto>> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken)
    {
        var cleanQuery = query.Trim();
        if (!IsEnabled || cleanQuery.Length < 2 || mediaType is LibraryMediaType.Manga or LibraryMediaType.Manhwa or LibraryMediaType.Anime)
            return Task.FromResult<IReadOnlyList<LibraryEntryDto>>([]);

        var cacheKey = $"{ProviderName}:search:{cleanQuery.ToLowerInvariant()}";
        var url = $"{BaseUrl}/fictions/search?title={Uri.EscapeDataString(cleanQuery)}";

        return ExecuteAsync(
            cacheKey,
            SearchCacheTtl,
            TimeSpan.FromSeconds(5),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                var html = await GetHtmlAsync(url, ct).ConfigureAwait(false);
                return html is null ? (IReadOnlyList<LibraryEntryDto>)[] : ParseSearchResults(html, ProviderName);
            },
            [],
            cancellationToken);
    }

    public Task<LibraryEntryDto?> GetDetailsAsync(string providerId, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
            return Task.FromResult<LibraryEntryDto?>(null);

        var cacheKey = $"{ProviderName}:details:{providerId}";
        var url = $"{BaseUrl}/fiction/{Uri.EscapeDataString(providerId)}";

        return ExecuteAsync(
            cacheKey,
            DetailsCacheTtl,
            TimeSpan.FromSeconds(10),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                var html = await GetHtmlAsync(url, ct).ConfigureAwait(false);
                return html is null ? null : ParseFictionPage(html, providerId, ProviderName);
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
            Logger.LogWarning("RoyalRoad returned non-success code: {Status} for {Url}", response.StatusCode, url);
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public static IReadOnlyList<LibraryEntryDto> ParseSearchResults(string html, string providerName)
    {
        var results = new List<LibraryEntryDto>();
        foreach (var cardBlock in SplitFictionCards(html))
        {
            var titleLinkMatch = TitleLinkRegex().Match(cardBlock);
            if (!titleLinkMatch.Success)
                continue;

            var id = titleLinkMatch.Groups["id"].Value;
            var slug = titleLinkMatch.Groups["slug"].Value;
            var title = CleanText(StripTags(titleLinkMatch.Groups["title"].Value));
            if (id.Length == 0 || title.Length == 0)
                continue;

            var coverMatch = CoverImageRegex().Match(cardBlock);
            var coverUrl = coverMatch.Success ? WebUtility.HtmlDecode(coverMatch.Groups["src"].Value) : null;

            var descMatch = DescriptionRegex().Match(cardBlock);
            var synopsis = descMatch.Success ? CleanText(StripTags(descMatch.Groups["text"].Value)) : null;

            var genres = TagRegex().Matches(cardBlock)
                .Select(m => CleanText(StripTags(m.Groups["text"].Value)))
                .Where(t => t.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            results.Add(new LibraryEntryDto(
                providerName,
                id,
                title,
                [],
                [],
                LibraryMediaType.Webnovel,
                coverUrl,
                synopsis,
                genres,
                null,
                null,
                null,
                null,
                null,
                $"{BaseUrl}/fiction/{id}/{slug}"));
        }

        return results;
    }

    /// <summary>Splits the search-results HTML into one chunk per "fiction-list-item" card so each
    /// field regex only ever runs against a single fiction's markup.</summary>
    private static List<string> SplitFictionCards(string html)
    {
        var starts = FictionCardStartRegex().Matches(html).Select(m => m.Index).ToList();
        var cards = new List<string>();
        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i];
            var end = i + 1 < starts.Count ? starts[i + 1] : html.Length;
            cards.Add(html[start..end]);
        }

        return cards;
    }

    public static LibraryEntryDto? ParseFictionPage(string html, string providerId, string providerName)
    {
        var title = OgPropertyRegex("og:title").Match(html) is { Success: true } titleMatch
            ? WebUtility.HtmlDecode(titleMatch.Groups["content"].Value).Trim()
            : null;
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var coverUrl = OgPropertyRegex("og:image").Match(html) is { Success: true } imgMatch
            ? WebUtility.HtmlDecode(imgMatch.Groups["content"].Value).Trim()
            : null;

        var synopsis = OgPropertyRegex("og:description").Match(html) is { Success: true } descMatch
            ? WebUtility.HtmlDecode(descMatch.Groups["content"].Value).Trim()
            : null;

        var genres = TagRegex().Matches(html)
            .Select(m => CleanText(StripTags(m.Groups["text"].Value)))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var status = FictionStatusRegex().Match(html) is { Success: true } statusMatch
            ? CleanText(statusMatch.Groups["status"].Value)
            : null;

        var slug = SlugFromTitle(title);
        return new LibraryEntryDto(
            providerName,
            providerId,
            title,
            [],
            [],
            LibraryMediaType.Webnovel,
            coverUrl,
            synopsis,
            genres,
            null,
            status,
            null,
            null,
            null,
            $"{BaseUrl}/fiction/{providerId}/{slug}");
    }

    private static string SlugFromTitle(string title) =>
        NonSlugCharRegex().Replace(title.ToLowerInvariant(), "-").Trim('-');

    private static string StripTags(string value) => Regex.Replace(value, "<.*?>", " ", RegexOptions.Singleline);

    private static string CleanText(string value) => WhitespaceRegex().Replace(WebUtility.HtmlDecode(value), " ").Trim();

    [GeneratedRegex("""(?is)<div\b[^>]*class\s*=\s*['"][^'"]*fiction-list-item[^'"]*['"][^>]*>""")]
    private static partial Regex FictionCardStartRegex();

    [GeneratedRegex(
        """(?is)<h2\b[^>]*class\s*=\s*['"][^'"]*fiction-title[^'"]*['"][^>]*>\s*<a\b[^>]*href\s*=\s*['"]/fiction/(?<id>\d+)/(?<slug>[^'"/]+)['"][^>]*>(?<title>.*?)</a>""")]
    private static partial Regex TitleLinkRegex();

    [GeneratedRegex("""(?is)<img\b[^>]*src\s*=\s*['"](?<src>[^'"]+)['"][^>]*class\s*=\s*['"][^'"]*cover[^'"]*['"]|<img\b[^>]*class\s*=\s*['"][^'"]*cover[^'"]*['"][^>]*src\s*=\s*['"](?<src>[^'"]+)['"]""")]
    private static partial Regex CoverImageRegex();

    [GeneratedRegex("""(?is)<div\b[^>]*class\s*=\s*['"][^'"]*fiction-description[^'"]*['"][^>]*>(?<text>.*?)</div>""")]
    private static partial Regex DescriptionRegex();

    [GeneratedRegex("""(?is)<a\b[^>]*class\s*=\s*['"][^'"]*fiction-tag[^'"]*['"][^>]*>(?<text>.*?)</a>""")]
    private static partial Regex TagRegex();

    [GeneratedRegex("""(?is)<span\b[^>]*class\s*=\s*['"][^'"]*label[^'"]*['"][^>]*>\s*(?<status>ONGOING|COMPLETED|HIATUS|STUB|DROPPED)\s*</span>""")]
    private static partial Regex FictionStatusRegex();

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonSlugCharRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private static Regex OgPropertyRegex(string property) => new(
        $"""(?is)<meta\s+property\s*=\s*['"]{Regex.Escape(property)}['"]\s+content\s*=\s*['"](?<content>[^'"]*)['"]""");
}
