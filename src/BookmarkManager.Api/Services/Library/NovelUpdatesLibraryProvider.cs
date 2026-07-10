using System.Net;
using System.Text.RegularExpressions;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BookmarkManager.Api.Services.Library;

/// <summary>
/// NovelUpdates has no official API and actively runs Cloudflare protection, so backend scraping
/// can hit a captcha wall under load. Off by default (<see cref="LibraryProviderOptions.EnableNovelUpdates"/>);
/// if backend scraping proves unreliable in practice, the documented fallback is fetching the HTML
/// via the Brave extension (already authenticated/cookied in the user's browser) and posting it here
/// for parsing - <see cref="ParseSeriesPage"/> and <see cref="ParseSearchResults"/> operate on raw
/// HTML so that fallback source is a drop-in swap for the HTTP fetch, not a rewrite.
/// </summary>
public sealed partial class NovelUpdatesLibraryProvider(
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    ILogger<NovelUpdatesLibraryProvider> logger,
    IOptions<LibraryProviderOptions> options)
    : LibraryMediaProviderBase(httpFactory, cache, logger), IMediaProvider
{
    private const string BaseUrl = "https://www.novelupdates.com";
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DetailsCacheTtl = TimeSpan.FromHours(3);

    private static readonly ProviderRateLimiter RateLimiter = new(
        tokenLimit: 2,
        tokensPerPeriod: 1,
        replenishmentPeriod: TimeSpan.FromSeconds(3));

    public override string ProviderName => "NovelUpdates";

    public bool IsEnabled => options.Value.EnableNovelUpdates;

    public Task<IReadOnlyList<LibraryEntryDto>> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken)
    {
        var cleanQuery = query.Trim();
        if (!IsEnabled || cleanQuery.Length < 2 || mediaType is LibraryMediaType.Manga or LibraryMediaType.Manhwa or LibraryMediaType.Anime)
            return Task.FromResult<IReadOnlyList<LibraryEntryDto>>([]);

        var cacheKey = $"{ProviderName}:search:{cleanQuery.ToLowerInvariant()}";
        var url = $"{BaseUrl}/?s={Uri.EscapeDataString(cleanQuery)}&post_type=seriesplans";

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
        var url = BuildAbsoluteUrl(providerId);

        return ExecuteAsync(
            cacheKey,
            DetailsCacheTtl,
            TimeSpan.FromSeconds(10),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                var html = await GetHtmlAsync(url, ct).ConfigureAwait(false);
                return html is null ? null : ParseSeriesPage(html, providerId, ProviderName);
            },
            null,
            cancellationToken);
    }

    public async Task<LibraryReleaseInfo?> GetLatestReleaseAsync(string providerId, CancellationToken cancellationToken)
    {
        var entry = await GetDetailsAsync(providerId, cancellationToken).ConfigureAwait(false);
        return entry is null
            ? null
            : new LibraryReleaseInfo(entry.LatestChapter, null, entry.LastReleaseAt, entry.SourceUrl);
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
            Logger.LogWarning("NovelUpdates returned non-success code: {Status} for {Url}", response.StatusCode, url);
            return null;
        }

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    public static IReadOnlyList<LibraryEntryDto> ParseSearchResults(string html, string providerName)
    {
        var results = new List<LibraryEntryDto>();
        foreach (Match match in SearchTitleRegex().Matches(html))
        {
            var href = WebUtility.HtmlDecode(match.Groups["href"].Value).Trim();
            var title = CleanText(StripTags(match.Groups["title"].Value));
            var providerId = ExtractSlugFromSeriesUrl(href);
            if (providerId is null || title.Length == 0)
                continue;

            if (results.Any(r => string.Equals(r.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)))
                continue;

            results.Add(new LibraryEntryDto(
                providerName,
                providerId,
                title,
                [],
                [],
                LibraryMediaType.Webnovel,
                null,
                null,
                [],
                null,
                null,
                null,
                null,
                null,
                BuildAbsoluteUrl(providerId)));
        }

        return results;
    }

    public static LibraryEntryDto? ParseSeriesPage(string html, string providerId, string providerName)
    {
        var title = SeriesTitleRegex().Match(html) is { Success: true } titleMatch
            ? CleanText(StripTags(titleMatch.Groups["title"].Value))
            : null;
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var coverUrl = CoverImageRegex().Match(html) is { Success: true } coverMatch
            ? WebUtility.HtmlDecode(coverMatch.Groups["src"].Value)
            : null;

        var synopsis = DescriptionRegex().Match(html) is { Success: true } descMatch
            ? CleanText(StripTags(descMatch.Groups["text"].Value))
            : null;

        var genres = AnchorTextsFromElement(html, "seriesgenre");

        var authors = AnchorTextsFromElement(html, "showauthors");

        var latestChapterMatch = LatestChapterRegex().Match(html);
        var latestChapter = latestChapterMatch.Success ? CleanText(StripTags(latestChapterMatch.Groups["text"].Value)) : null;

        return new LibraryEntryDto(
            providerName,
            providerId,
            title,
            [],
            authors,
            LibraryMediaType.Webnovel,
            coverUrl,
            synopsis,
            genres,
            null,
            null,
            latestChapter,
            null,
            null,
            BuildAbsoluteUrl(providerId));
    }

    private static List<string> AnchorTextsFromElement(string html, string id)
    {
        var block = ExtractElementBlockById(html, id);
        if (block is null)
            return [];

        return AnchorTextRegex().Matches(block)
            .Select(m => CleanText(StripTags(m.Groups["text"].Value)))
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ExtractElementBlockById(string html, string id)
    {
        var idMatch = Regex.Match(
            html,
            $"<(?<tag>[a-zA-Z][a-zA-Z0-9]*)\\b[^>]*\\bid\\s*=\\s*['\"]{Regex.Escape(id)}['\"][^>]*>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!idMatch.Success)
            return null;

        var tagName = idMatch.Groups["tag"].Value;
        var start = idMatch.Index;
        var closeMatch = Regex.Match(
            html[idMatch.Index..],
            $"</{Regex.Escape(tagName)}>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        return closeMatch.Success
            ? html.Substring(start, closeMatch.Index + closeMatch.Length)
            : html[start..];
    }

    private static string? ExtractSlugFromSeriesUrl(string href)
    {
        var match = SeriesUrlRegex().Match(href);
        return match.Success ? match.Groups["slug"].Value.Trim('/') : null;
    }

    private static string BuildAbsoluteUrl(string providerId) => $"{BaseUrl}/series/{providerId}/";

    private static string StripTags(string value) => Regex.Replace(value, "<.*?>", " ", RegexOptions.Singleline);

    private static string CleanText(string value) => WhitespaceRegex().Replace(WebUtility.HtmlDecode(value), " ").Trim();

    [GeneratedRegex("""(?is)<(?:div|h[1-6])\b[^>]*class\s*=\s*['"][^'" >]*search_title[^'" >]*['"][^>]*>\s*<a\b[^>]*href\s*=\s*['"](?<href>[^'"]+)['"][^>]*>(?<title>.*?)</a>""")]
    private static partial Regex SearchTitleRegex();

    [GeneratedRegex("""(?is)/series/(?<slug>[^'"/?#]+)""")]
    private static partial Regex SeriesUrlRegex();

    [GeneratedRegex("""(?is)<div\b[^>]*class\s*=\s*['"][^'"]*seriestitlenu[^'"]*['"][^>]*>(?<title>.*?)</div>""")]
    private static partial Regex SeriesTitleRegex();

    [GeneratedRegex("""(?is)<div\b[^>]*class\s*=\s*['"][^'"]*seriesimg[^'"]*['"][^>]*>\s*<img\b[^>]*src\s*=\s*['"](?<src>[^'"]+)['"]""")]
    private static partial Regex CoverImageRegex();

    [GeneratedRegex("""(?is)<div\b[^>]*id\s*=\s*['"]editdescription['"][^>]*>(?<text>.*?)</div>""")]
    private static partial Regex DescriptionRegex();

    [GeneratedRegex("""(?is)<a\b[^>]*>(?<text>.*?)</a>""")]
    private static partial Regex AnchorTextRegex();

    [GeneratedRegex("""(?is)<table\b[^>]*id\s*=\s*['"]myTable['"][^>]*>.*?<a\b[^>]*>(?<text>.*?)</a>""")]
    private static partial Regex LatestChapterRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
