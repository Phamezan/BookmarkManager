using System.Text.RegularExpressions;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public static partial class BookmarkTagClassifier
{
    public static BookmarkTagClassification Classify(
        string title,
        string? url,
        string? folderPath,
        BookmarkTagDomainDto requestedDomain)
    {
        var cleanTitle = CleanTitle(title, url);
        var folderTokens = Tokenize(folderPath);
        var folderText = NormalizeForPhraseMatching(folderPath);
        var urlText = (url ?? string.Empty).ToLowerInvariant();

        if (requestedDomain == BookmarkTagDomainDto.Anime)
            return new(BookmarkTagDomain.Anime, cleanTitle, ShouldUseAniList: true, ShouldUseMangaUpdates: false, IsEligibleForDualProviderLookup: false, "user selected Anime");
        if (requestedDomain == BookmarkTagDomainDto.Manga)
            return new(BookmarkTagDomain.Manga, cleanTitle, ShouldUseAniList: false, ShouldUseMangaUpdates: true, IsEligibleForDualProviderLookup: false, "user selected Manga");
        if (requestedDomain == BookmarkTagDomainDto.Novel)
            return new(BookmarkTagDomain.Novel, cleanTitle, ShouldUseAniList: false, ShouldUseMangaUpdates: true, IsEligibleForDualProviderLookup: false, "user selected Novel");
        if (requestedDomain == BookmarkTagDomainDto.General)
            return new(BookmarkTagDomain.General, cleanTitle, ShouldUseAniList: false, ShouldUseMangaUpdates: false, IsEligibleForDualProviderLookup: false, "user selected General");

        if (HasAnyToken(folderTokens, "anime") || HasAnyUrlSignal(urlText,
                "crunchyroll", "animepahe", "gogoanime", "9anime", "9animetv", "anilist.co/anime", "myanimelist.net/anime",
                "miruro", "aniwatch", "aniwave", "zoro", "zorox", "hianime", "animesge", "kickassanime", "allanime"))
        {
            return new(BookmarkTagDomain.Anime, cleanTitle, ShouldUseAniList: true, ShouldUseMangaUpdates: false, IsEligibleForDualProviderLookup: false, "anime folder/host signal");
        }

        if (HasAnyToken(folderTokens, "manga", "manhwa", "manhua", "webtoon") || HasAnyUrlSignal(urlText,
                "mangadex", "asuracomic", "comick", "mangaplus", "webtoons.com", "mangakakalot",
                "manga", "manhwa", "manhua", "webtoon", "reaperscans"))
        {
            return new(BookmarkTagDomain.Manga, cleanTitle, ShouldUseAniList: false, ShouldUseMangaUpdates: true, IsEligibleForDualProviderLookup: false, "manga folder/host signal");
        }

        if (HasNovelFolderSignal(folderText, folderTokens) || 
            HasAnyUrlSignal(urlText, "novel", "royalroad", "scribblehub", "wuxiaworld", "/ln/", "/wn/") ||
            HasNovelTitleSignal(title))
        {
            return new(BookmarkTagDomain.Novel, cleanTitle, ShouldUseAniList: false, ShouldUseMangaUpdates: true, IsEligibleForDualProviderLookup: false, "novel folder/host/title signal");
        }

        var isEligible = !IsNonMediaUrl(urlText);
        return new(BookmarkTagDomain.General, cleanTitle, ShouldUseAniList: false, ShouldUseMangaUpdates: false, IsEligibleForDualProviderLookup: isEligible, "no domain-specific folder/host signal");
    }

    public static BookmarkTagDomainDto GuessDefaultDomainFromFolderTitle(string folderTitleOrPath)
    {
        var tokens = Tokenize(folderTitleOrPath);
        var text = NormalizeForPhraseMatching(folderTitleOrPath);
        if (HasAnyToken(tokens, "anime")) return BookmarkTagDomainDto.Anime;
        if (HasAnyToken(tokens, "manga", "manhwa", "manhua", "webtoon")) return BookmarkTagDomainDto.Manga;
        if (HasNovelFolderSignal(text, tokens)) return BookmarkTagDomainDto.Novel;
        return BookmarkTagDomainDto.General;
    }

    private static bool HasNovelFolderSignal(string normalizedText, IReadOnlySet<string> tokens)
    {
        if (HasPhrase(normalizedText, "light novel") || HasPhrase(normalizedText, "light novels") ||
            HasPhrase(normalizedText, "web novel") || HasPhrase(normalizedText, "web novels"))
        {
            return true;
        }

        return HasAnyToken(tokens, "novel", "novels", "noveller", "novelle", "ln", "wn", "wuxia", "xianxia", "ranobe");
    }

    private static bool HasPhrase(string normalizedText, string phrase)
        => normalizedText.Length > 0 && $" {normalizedText} ".Contains($" {phrase} ", StringComparison.Ordinal);

    private static bool HasAnyToken(IReadOnlySet<string> tokens, params string[] needles)
        => needles.Any(tokens.Contains);

    private static bool IsNonMediaUrl(string urlText)
    {
        return HasAnyUrlSignal(urlText,
            "github.com", "gitlab.com", "bitbucket.org",
            "stackoverflow.com", "stackexchange.com",
            "amazon.com", "amazon.co.uk", "amazon.ca", "amazon.co.jp", "amazon.de",
            "ebay.com", "aliexpress.com", "target.com", "walmart.com",
            "youtube.com", "youtu.be", "vimeo.com", "twitch.tv",
            "wikipedia.org", "wiktionary.org",
            "reddit.com", "twitter.com", "x.com",
            "google.com", "bing.com", "yahoo.com",
            "microsoft.com", "apple.com", "netflix.com", "spotify.com");
    }

    private static bool HasAnyUrlSignal(string value, params string[] needles)
        => needles.Any(value.Contains);

    private static IReadOnlySet<string> Tokenize(string? value)
        => TokenRegex().Matches(value ?? string.Empty)
            .Select(match => match.Value.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeForPhraseMatching(string? value)
        => WhitespaceRegex().Replace(TokenSeparatorRegex().Replace(value ?? string.Empty, " ").ToLowerInvariant(), " ").Trim();

    private static readonly string[] SiteKeywords = new[]
    {
        "scans", "scan", "translations", "translation", "novel", "novels", "manga", "anime", 
        "webtoon", "webtoons", "read", "watch", "official", "site", "cool", "usb", "hub", "full", 
        "updates", "scanlation", "scanlations", "scantrad", "raw", "raws",
        "ads", "ad", "popup", "popups", "pop-up", "pop-ups", "pop-ads"
    };

    private static bool IsSiteSegment(string segment, string? url)
    {
        var normalized = segment.ToLowerInvariant().Trim();
        
        if (normalized.EndsWith(".com") || normalized.EndsWith(".org") || normalized.EndsWith(".net") ||
            normalized.EndsWith(".co") || normalized.EndsWith(".me") || normalized.EndsWith(".info") ||
            normalized.EndsWith(".xyz") || normalized.EndsWith(".to") || normalized.EndsWith(".tv") ||
            normalized.Contains(".com/") || normalized.Contains(".org/"))
        {
            return true;
        }

        var tokens = Tokenize(segment);
        if (SiteKeywords.Any(tokens.Contains))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(url))
        {
            try
            {
                var uri = new Uri(url);
                var host = uri.Host.ToLowerInvariant();
                var hostParts = host.Split('.');
                foreach (var part in hostParts)
                {
                    if (part.Length > 3 && normalized.Contains(part))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Ignore invalid URIs
            }
        }

        return false;
    }

    private static string CleanInnerSegment(string segment)
    {
        var result = segment.Trim();
        
        if (result.StartsWith("read online ", StringComparison.OrdinalIgnoreCase))
            result = result.Substring(12).Trim();
        else if (result.StartsWith("watch online ", StringComparison.OrdinalIgnoreCase))
            result = result.Substring(13).Trim();
        else if (result.StartsWith("read ", StringComparison.OrdinalIgnoreCase))
            result = result.Substring(5).Trim();
        else if (result.StartsWith("watch ", StringComparison.OrdinalIgnoreCase))
            result = result.Substring(6).Trim();

        if (result.EndsWith(" online for free", StringComparison.OrdinalIgnoreCase))
            result = result.Substring(0, result.Length - 16).Trim();
        else if (result.EndsWith(" free online", StringComparison.OrdinalIgnoreCase))
            result = result.Substring(0, result.Length - 12).Trim();
        else if (result.EndsWith(" online", StringComparison.OrdinalIgnoreCase))
            result = result.Substring(0, result.Length - 7).Trim();
        else if (result.EndsWith(" for free", StringComparison.OrdinalIgnoreCase))
            result = result.Substring(0, result.Length - 9).Trim();
        else if (result.EndsWith(" free", StringComparison.OrdinalIgnoreCase))
            result = result.Substring(0, result.Length - 5).Trim();

        return result;
    }

    public static string CleanTitle(string title, string? url = null)
    {
        return MediaTitleNormalizer.CleanTitle(title, url);
    }

    private static bool HasNovelTitleSignal(string title)
    {
        var lower = title.ToLowerInvariant();
        return lower.Contains("light novel") || lower.Contains("web novel") || 
               lower.Contains("novel updates") || lower.Contains("novel cool") ||
               lower.Contains("novelusb") || lower.Contains("scribble hub") ||
               lower.Contains("scribblehub") || lower.Contains("novel-book");
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^\)]*\)")]
    private static partial Regex BracketedTextRegex();

    [GeneratedRegex(@"(?i)\b(?:episode|ep|chapter|ch|vol|volume|season|s)\.?\s*\d+(?:\.\d+)?\b")]
    private static partial Regex EpisodeChapterSuffixRegex();

    [GeneratedRegex(@"(?i)\s+[-|:]\s+(?:novel updates|webtoon xyz|read online|official site|home)$")]
    private static partial Regex SiteSuffixRegex();

    [GeneratedRegex(@"\s+\d+(?:\.\d+)?$")]
    private static partial Regex TrailingNumberRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex TokenSeparatorRegex();
}
