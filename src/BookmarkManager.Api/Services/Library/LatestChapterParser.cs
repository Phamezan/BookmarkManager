using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.Library;

/// <inheritdoc cref="LibraryLatestChapterParser"/>
public static class LatestChapterParser
{
    public static double? Parse(string? latestChapter) => LibraryLatestChapterParser.Parse(latestChapter);
}
