using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Features.Library;

internal static class LibraryProgressDisplay
{
    public static string? BadgeText(LibraryReadingProgressDto? progress)
    {
        if (progress is not { CurrentChapter: { } current })
            return null;

        return progress.LatestChapterNumber is { } latest
            ? $"{FormatChapter(current)}/{FormatChapter(latest)}"
            : progress.RawProgressText;
    }

    public static string FormatChapter(double value) =>
        value == Math.Floor(value) ? value.ToString("0") : value.ToString("0.#");
}
