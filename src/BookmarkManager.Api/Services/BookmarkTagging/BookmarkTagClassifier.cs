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
        var cleanTitle = CleanTitle(title);
        var combined = $" {title} {url} {folderPath} ".ToLowerInvariant();

        if (requestedDomain == BookmarkTagDomainDto.Anime)
            return new(BookmarkTagDomain.Anime, cleanTitle, ShouldUseAniList: true, ShouldUseMangaUpdates: false, "user selected Anime");
        if (requestedDomain == BookmarkTagDomainDto.Manga)
            return new(BookmarkTagDomain.Manga, cleanTitle, ShouldUseAniList: false, ShouldUseMangaUpdates: true, "user selected Manga");
        if (requestedDomain == BookmarkTagDomainDto.Novel)
            return new(BookmarkTagDomain.Novel, cleanTitle, ShouldUseAniList: false, ShouldUseMangaUpdates: true, "user selected Novel");
        if (requestedDomain == BookmarkTagDomainDto.General)
            return new(BookmarkTagDomain.General, cleanTitle, ShouldUseAniList: false, ShouldUseMangaUpdates: false, "user selected General");

        if (ContainsAny(combined,
                " anime ", "anime/", "/anime", "crunchyroll", "animepahe", "gogoanime", "9anime", "9animetv", "anilist.co/anime", "myanimelist.net/anime",
                "miruro", "aniwatch", "aniwave", "zoro", "zorox", "hianime", "animesge", "kickassanime", "allanime"))
        {
            return new(BookmarkTagDomain.Anime, cleanTitle, ShouldUseAniList: true, ShouldUseMangaUpdates: false, "anime folder/host signal");
        }

        if (ContainsAny(combined,
                " manga ", " manhwa ", " manhua ", " webtoon ", "mangadex", "asuracomic", "comick", "mangaplus", "webtoons.com", "mangakakalot"))
        {
            return new(BookmarkTagDomain.Manga, cleanTitle, ShouldUseAniList: false, ShouldUseMangaUpdates: true, "manga folder/host signal");
        }

        if (ContainsAny(combined,
                " novel ", " novels ", " light novel ", "lightnovel", " web novel ", "webnovel", "novelupdates", "novelbin", "royalroad", "wuxia", "xianxia", "ranobe", " ln ", "/ln/", " wn ", "/wn/"))
        {
            return new(BookmarkTagDomain.Novel, cleanTitle, ShouldUseAniList: false, ShouldUseMangaUpdates: true, "novel folder/host signal");
        }

        return new(BookmarkTagDomain.General, cleanTitle, ShouldUseAniList: false, ShouldUseMangaUpdates: false, "no domain-specific folder/host signal");
    }

    public static BookmarkTagDomainDto GuessDefaultDomainFromFolderTitle(string folderTitleOrPath)
    {
        var value = folderTitleOrPath.ToLowerInvariant();
        if (value.Contains("anime")) return BookmarkTagDomainDto.Anime;
        if (ContainsAny(value, "manga", "manhwa", "manhua", "webtoon")) return BookmarkTagDomainDto.Manga;
        if (ContainsAny(value, "novel", "novels", "lightnovel", "webnovel", "ranobe", "wuxia", "xianxia", " ln ", " wn ")) return BookmarkTagDomainDto.Novel;
        return BookmarkTagDomainDto.General;
    }

    private static bool ContainsAny(string value, params string[] needles)
        => needles.Any(value.Contains);

    public static string CleanTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        var clean = BracketedTextRegex().Replace(title, " ");
        clean = EpisodeChapterSuffixRegex().Replace(clean, " ");
        clean = SiteSuffixRegex().Replace(clean, " ");
        clean = WhitespaceRegex().Replace(clean, " ").Trim(' ', '-', '|', ':', '_', ',');
        return clean;
    }

    [GeneratedRegex(@"\[[^\]]*\]|\([^\)]*\)")]
    private static partial Regex BracketedTextRegex();

    [GeneratedRegex(@"(?i)\b(?:episode|ep|chapter|ch|vol|volume)\.?\s*\d+(?:\.\d+)?\b")]
    private static partial Regex EpisodeChapterSuffixRegex();

    [GeneratedRegex(@"(?i)\s+[-|:]\s+(?:novel updates|webtoon xyz|read online|official site|home)$")]
    private static partial Regex SiteSuffixRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
