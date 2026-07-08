using System.Collections.Concurrent;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public sealed partial class NovelUpdatesTaggingService : INovelUpdatesTagProvider
{
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan EmptyCacheDuration = TimeSpan.FromMinutes(30);
    private const double SimilarityThreshold = 0.60;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<NovelUpdatesTaggingService> _logger;
    private readonly ConcurrentDictionary<string, NovelUpdatesCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record NovelUpdatesCacheEntry(ProviderTagResult Result, DateTimeOffset ExpiresAt);
    private sealed record SearchCandidate(string Title, string Href);

    public NovelUpdatesTaggingService(IHttpClientFactory httpFactory, ILogger<NovelUpdatesTaggingService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    public Task<ProviderTagResult> GetTagsForTitleAsync(string title, string? url, BookmarkTagDomain domain, string? folderPath, CancellationToken cancellationToken)
        => GetTagsForTitleAsync(new MediaTagLookupContext(
            title,
            url,
            domain,
            folderPath,
            MediaTitleNormalizer.Normalize(title, url, domain)), cancellationToken);

    public async Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken)
    {
        if (context.Domain != BookmarkTagDomain.Novel)
            return new([], false, null);

        var candidate = context.NormalizedTitle.Candidates.FirstOrDefault()?.Query ?? context.OriginalTitle;
        var cleanQuery = MediaTitleNormalizer.BuildLooseQuery(candidate);
        if (string.IsNullOrWhiteSpace(cleanQuery) || cleanQuery.Length < 2)
            return new([], false, null);

        var now = DateTimeOffset.UtcNow;
        var cacheKey = $"{context.Domain}:{candidate}:{cleanQuery}";
        if (_cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
            return cached.Result;

        try
        {
            _logger.LogInformation(
                "Querying NovelUpdates tags. OriginalTitle='{OriginalTitle}', Host='{Host}', Domain={Domain}, Candidate='{Candidate}', QuerySentToProvider='{Query}'",
                context.OriginalTitle,
                context.NormalizedTitle.Host,
                context.Domain,
                candidate,
                cleanQuery);

            var result = await FetchTagsFromNovelUpdatesAsync(cleanQuery, candidate, cancellationToken).ConfigureAwait(false);
            _cache[cacheKey] = new NovelUpdatesCacheEntry(result, now.Add(result.Tags.Count == 0 ? EmptyCacheDuration : SuccessCacheDuration));
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query NovelUpdates for tags of '{Title}'", context.OriginalTitle);
            var emptyResult = new ProviderTagResult([], false, null);
            _cache[cacheKey] = new NovelUpdatesCacheEntry(emptyResult, now.Add(EmptyCacheDuration));
            return emptyResult;
        }
    }

    private async Task<ProviderTagResult> FetchTagsFromNovelUpdatesAsync(string cleanQuery, string scoreQuery, CancellationToken cancellationToken)
    {
        var http = _httpFactory.CreateClient(nameof(NovelUpdatesTaggingService));
        http.DefaultRequestHeaders.Clear();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

        var searchUrl = $"https://www.novelupdates.com/?s={Uri.EscapeDataString(cleanQuery)}&post_type=seriesplans";
        using var response = await http.GetAsync(searchUrl, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("NovelUpdates search returned non-success code: {Status}", response.StatusCode);
            return new([], false, null);
        }

        var searchHtml = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var candidates = ExtractSearchCandidates(searchHtml);
        var best = candidates
            .Select(candidate => new { Candidate = candidate, Score = ScoreNovelUpdatesCandidate(candidate.Title, scoreQuery) })
            .OrderByDescending(item => item.Score)
            .FirstOrDefault();

        if (best is null || best.Score < SimilarityThreshold)
        {
            var score = best?.Score ?? -1.0;
            return new([], false, $"Best candidate similarity ({score:F4}) was below similarity threshold {SimilarityThreshold:F2}.");
        }

        var detailsUrl = BuildAbsoluteNovelUpdatesUrl(best.Candidate.Href);
        using var detailsResponse = await http.GetAsync(detailsUrl, cancellationToken).ConfigureAwait(false);
        if (!detailsResponse.IsSuccessStatusCode)
        {
            _logger.LogWarning("NovelUpdates series page returned non-success code: {Status}", detailsResponse.StatusCode);
            return new([], false, null);
        }

        var detailsHtml = await detailsResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var tags = ExtractTags(detailsHtml);
        return new(tags, false, null);
    }

    public static List<string> ExtractTags(string html)
    {
        var tags = new List<string> { "Novel" };
        AddAnchorTextsFromElement(html, "seriesgenre", tags);
        AddAnchorTextsFromElement(html, "showtags", tags);
        AddSidebarFields(html, tags);

        return tags
            .Select(NormalizeTagText)
            .Where(tag => tag.Length > 1)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .ToList();
    }

    private static List<SearchCandidate> ExtractSearchCandidates(string html)
    {
        var candidates = new List<SearchCandidate>();

        foreach (Match match in SearchTitleRegex().Matches(html))
            AddCandidate(candidates, match.Groups["href"].Value, match.Groups["title"].Value);

        if (candidates.Count == 0)
        {
            foreach (Match match in SeriesAnchorRegex().Matches(html))
                AddCandidate(candidates, match.Groups["href"].Value, match.Groups["title"].Value);
        }

        return candidates;
    }

    private static void AddCandidate(List<SearchCandidate> candidates, string href, string title)
    {
        var cleanTitle = NormalizeTagText(StripTags(title));
        var cleanHref = WebUtility.HtmlDecode(href).Trim();
        if (cleanTitle.Length == 0 || cleanHref.Length == 0)
            return;

        if (!candidates.Any(existing => string.Equals(existing.Href, cleanHref, StringComparison.OrdinalIgnoreCase)))
            candidates.Add(new SearchCandidate(cleanTitle, cleanHref));
    }

    private static void AddAnchorTextsFromElement(string html, string id, List<string> tags)
    {
        var block = ExtractElementBlockById(html, id);
        if (block is null)
            return;

        foreach (Match match in AnchorTextRegex().Matches(block))
        {
            var tag = NormalizeTagText(StripTags(match.Groups["text"].Value));
            if (tag.Length > 0)
                tags.Add(tag);
        }
    }

    private static void AddSidebarFields(string html, List<string> tags)
    {
        foreach (Match match in SeriesOtherRegex().Matches(html))
        {
            var text = NormalizeTagText(StripTags(match.Groups["text"].Value));
            if (text.Length == 0)
                continue;

            var colonIndex = text.IndexOf(':', StringComparison.Ordinal);
            if (colonIndex < 0)
                continue;

            var name = text[..colonIndex].Trim();
            var value = text[(colonIndex + 1)..].Trim();
            if (value.Length == 0)
                continue;

            if (name.Equals("Type", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Language", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Original Publisher", StringComparison.OrdinalIgnoreCase))
            {
                tags.Add(value);
            }
        }
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

    private static double ScoreNovelUpdatesCandidate(string candidateTitle, string cleanQuery)
    {
        return TitleMatching.ScoreCandidates(cleanQuery, [candidateTitle]);
    }

    private static string BuildAbsoluteNovelUpdatesUrl(string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        return new Uri(new Uri("https://www.novelupdates.com"), href).ToString();
    }

    private static string StripTags(string value)
        => Regex.Replace(value, "<.*?>", " ", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private static string NormalizeTagText(string value)
    {
        var decoded = WebUtility.HtmlDecode(value);
        decoded = WhitespaceRegex().Replace(decoded, " ");
        return decoded.Trim();
    }

    [GeneratedRegex("""(?is)<(?:div|h[1-6])\b[^>]*class\s*=\s*['"][^'" >]*search_title[^'" >]*['"][^>]*>\s*<a\b[^>]*href\s*=\s*['"](?<href>[^'"]+)['"][^>]*>(?<title>.*?)</a>""")]
    private static partial Regex SearchTitleRegex();

    [GeneratedRegex("""(?is)<a\b[^>]*href\s*=\s*['"](?<href>[^'"]*/series/[^'"]+)['"][^>]*>(?<title>.*?)</a>""")]
    private static partial Regex SeriesAnchorRegex();

    [GeneratedRegex("""(?is)<a\b[^>]*>(?<text>.*?)</a>""")]
    private static partial Regex AnchorTextRegex();

    [GeneratedRegex("""(?is)<div\b[^>]*class\s*=\s*['"][^'"]*seriesother[^'"]*['"][^>]*>(?<text>.*?)</div>""")]
    private static partial Regex SeriesOtherRegex();



    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
