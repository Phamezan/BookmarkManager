using System;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.BookmarkTagging;

internal sealed record MediaCandidateClassification(
    bool RequiresAi,
    string CanonicalTitle,
    BookmarkTagDomain Domain,
    string Reason);

internal static class BookmarkMediaCandidateClassifier
{
    public static MediaCandidateClassification Classify(string title, string? url, string? folderPath)
    {
        var host = GetHost(url);
        var path = folderPath ?? string.Empty;

        // 1. Novel
        if (host != null && (host.Contains("novelupdates.com", StringComparison.OrdinalIgnoreCase) || host.Contains("novelfull.com", StringComparison.OrdinalIgnoreCase)))
        {
            var clean = MediaTitleNormalizer.CleanTitle(title, url, BookmarkTagDomain.Novel);
            return new MediaCandidateClassification(false, clean, BookmarkTagDomain.Novel, $"Matched Novel host: {host}");
        }
        if (path.Contains("Novel", StringComparison.OrdinalIgnoreCase) || 
            path.Contains("Light Novel", StringComparison.OrdinalIgnoreCase) || 
            path.Contains("Web Novel", StringComparison.OrdinalIgnoreCase))
        {
            var clean = MediaTitleNormalizer.CleanTitle(title, url, BookmarkTagDomain.Novel);
            return new MediaCandidateClassification(false, clean, BookmarkTagDomain.Novel, $"Folder path contains Novel pattern: {path}");
        }

        // 2. Manga
        if (host != null && host.Contains("mangaupdates.com", StringComparison.OrdinalIgnoreCase))
        {
            var clean = MediaTitleNormalizer.CleanTitle(title, url, BookmarkTagDomain.Manga);
            return new MediaCandidateClassification(false, clean, BookmarkTagDomain.Manga, $"Matched Manga host: {host}");
        }
        if (path.Contains("Manga", StringComparison.OrdinalIgnoreCase) || 
            path.Contains("Manhwa", StringComparison.OrdinalIgnoreCase) || 
            path.Contains("Manhua", StringComparison.OrdinalIgnoreCase))
        {
            var clean = MediaTitleNormalizer.CleanTitle(title, url, BookmarkTagDomain.Manga);
            return new MediaCandidateClassification(false, clean, BookmarkTagDomain.Manga, $"Folder path contains Manga pattern: {path}");
        }

        // 3. Anime
        if (path.Contains("Anime", StringComparison.OrdinalIgnoreCase))
        {
            var clean = MediaTitleNormalizer.CleanTitle(title, url, BookmarkTagDomain.Anime);
            return new MediaCandidateClassification(false, clean, BookmarkTagDomain.Anime, $"Folder path contains Anime pattern: {path}");
        }

        // 4. Default / Ambiguous
        return new MediaCandidateClassification(true, title, BookmarkTagDomain.General, "Requires AI identification");
    }

    private static string? GetHost(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return uri.Host;
        }
        return null;
    }
}
