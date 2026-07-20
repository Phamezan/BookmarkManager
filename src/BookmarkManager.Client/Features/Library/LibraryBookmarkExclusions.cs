using System.Text.RegularExpressions;

namespace BookmarkManager.Client.Features.Library;

/// <summary>Compact bookmark fingerprints so Library recommends can deprioritize series the user
/// already tracks without a round-trip per catalog row.</summary>
public sealed partial class LibraryBookmarkExclusions
{
    public static LibraryBookmarkExclusions Empty { get; } = new([], []);

    private readonly HashSet<string> _seriesKeys;
    private readonly HashSet<string> _normalizedTitles;

    private LibraryBookmarkExclusions(IEnumerable<string> seriesKeys, IEnumerable<string> normalizedTitles)
    {
        _seriesKeys = seriesKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _normalizedTitles = normalizedTitles.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static LibraryBookmarkExclusions FromBookmarks(IEnumerable<BookmarkSignal> bookmarks)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var bookmark in bookmarks)
        {
            foreach (var key in SeriesKeysFromBookmark(bookmark))
                keys.Add(key);

            var normalized = NormalizeTitle(bookmark.Title);
            if (normalized.Length >= 4)
                titles.Add(normalized);
        }

        return new LibraryBookmarkExclusions(keys, titles);
    }

    public bool Contains(LibraryItem item)
    {
        foreach (var key in SeriesKeysFromItem(item))
        {
            if (_seriesKeys.Contains(key))
                return true;
        }

        var title = NormalizeTitle(item.Title);
        return title.Length >= 4 && _normalizedTitles.Contains(title);
    }

    private static IEnumerable<string> SeriesKeysFromItem(LibraryItem item)
    {
        foreach (var key in LibrarySeriesKey.FromProviderKeys(item.Provider, item.ProviderId))
            yield return key;

        if (LibrarySeriesKey.FromUrl(item.SourceUrl) is { } urlKey)
            yield return urlKey;
    }

    private static IEnumerable<string> SeriesKeysFromBookmark(BookmarkSignal bookmark)
    {
        if (bookmark.AniListId is > 0)
        {
            yield return $"anilist|{bookmark.AniListId.Value}";
            yield return $"anilist.co|{bookmark.AniListId.Value}";
        }

        if (LibrarySeriesKey.FromUrl(bookmark.Url) is { } urlKey)
            yield return urlKey;
    }

    private static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var cleaned = TitleNoiseRegex().Replace(title.ToLowerInvariant(), " ");
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex(@"[^\p{L}\p{Nd}\s]+", RegexOptions.Compiled)]
    private static partial Regex TitleNoiseRegex();
}

public readonly record struct BookmarkSignal(string? Url, string Title, int? AniListId);

internal static partial class LibrarySeriesKey
{
    private static readonly Dictionary<string, string> ProviderHostAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Novelfire"] = "novelfire.net",
            ["AniList"] = "anilist.co",
            ["MangaDex"] = "mangadex.org",
            ["Kitsu"] = "kitsu.io",
            ["RanobeDB"] = "ranobedb.org",
            ["RoyalRoad"] = "royalroad.com",
        };

    private static readonly (string HostFragment, Regex PathRegex)[] HostSeriesPatterns =
    [
        ("novelfire", BookSlugRegex()),
        ("royalroad", FictionIdRegex()),
        ("mangadex", TitleUuidRegex()),
        ("anilist.co", AniListIdRegex()),
    ];

    public static IEnumerable<string> FromProviderKeys(string provider, string providerId)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerId))
            yield break;

        var normalizedProvider = provider.Trim().ToLowerInvariant();
        var normalizedId = providerId.Trim().ToLowerInvariant();
        yield return $"{normalizedProvider}|{normalizedId}";

        if (ProviderHostAliases.TryGetValue(provider, out var host))
            yield return $"{host}|{normalizedId}";
    }

    public static string? FromUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.TrimEnd('/');

        foreach (var (fragment, pattern) in HostSeriesPatterns)
        {
            if (!host.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                continue;

            var match = pattern.Match(path);
            if (!match.Success)
                continue;

            var id = match.Groups["id"].Value.ToLowerInvariant();
            return $"{host}|{id}";
        }

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return null;

        var seriesSegment = StripChapterSegment(segments[^1]) ? segments[..^1] : segments;
        if (seriesSegment.Length == 0)
            seriesSegment = segments;

        var compact = string.Join('/', seriesSegment.Take(3)).ToLowerInvariant();
        return compact.Length == 0 ? null : $"{host}|{compact}";
    }

    private static bool StripChapterSegment(string lastSegment) =>
        ChapterSegmentRegex().IsMatch(lastSegment);

    [GeneratedRegex(@"(?i)^(?:chapter|ch|vol|volume|c)[-_\s]?\d+")]
    private static partial Regex ChapterSegmentRegex();

    [GeneratedRegex(@"(?i)^/book/(?<id>[^/]+)")]
    private static partial Regex BookSlugRegex();

    [GeneratedRegex(@"(?i)^/fiction/(?<id>\d+)")]
    private static partial Regex FictionIdRegex();

    [GeneratedRegex(@"(?i)^/title/(?<id>[0-9a-f-]{36})")]
    private static partial Regex TitleUuidRegex();

    [GeneratedRegex(@"(?i)^/(?:anime|manga)/(?<id>\d+)")]
    private static partial Regex AniListIdRegex();
}
