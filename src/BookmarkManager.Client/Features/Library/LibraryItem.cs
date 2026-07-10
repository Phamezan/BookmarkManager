using System.Globalization;
using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Features.Library;

/// <summary>
/// One catalog entry in the Library — wraps a provider search result plus persisted,
/// manager-only tracking state. Immutable, so updates produce a new record via <c>with</c>.
/// </summary>
public sealed record LibraryItem(
    string Provider,
    string ProviderId,
    string Title,
    IReadOnlyList<string> Authors,
    LibraryMediaType Type,
    string? Synopsis,
    IReadOnlyList<string> Genres,
    double? Rating,
    string? Status,
    string? LatestChapter,
    DateTimeOffset? LastReleaseAt,
    string? CoverImageUrl,
    string SourceUrl,
    bool IsTrending = false,
    bool IsTracked = false,
    double? ChaptersRead = null,
    Guid? BookmarkId = null,
    string? LatestChapterUrl = null)
{
    public static LibraryItem FromDto(LibraryEntryDto dto, bool isTrending = false) => new(
        dto.Provider,
        dto.ProviderId,
        dto.Title,
        dto.Authors,
        dto.MediaType,
        dto.Synopsis,
        dto.Genres,
        dto.Rating,
        dto.Status,
        dto.LatestChapter,
        dto.LastReleaseAt,
        dto.CoverImageUrl,
        dto.SourceUrl,
        isTrending);

    public string Author => Authors.Count > 0 ? string.Join(", ", Authors) : "Unknown";

    private double? LatestChapterNumber =>
        LatestChapter is not null && double.TryParse(LatestChapter, NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    public double? ChaptersBehind =>
        IsTracked && LatestChapterNumber is { } latest && ChaptersRead is { } read
            ? Math.Max(0, latest - read)
            : null;

    public string TypeLabel => Type switch
    {
        LibraryMediaType.LightNovel => "Light Novel",
        _ => Type.ToString()
    };

    public string TypeClass => Type switch
    {
        LibraryMediaType.Manga => "lib-type-manga",
        LibraryMediaType.Manhwa => "lib-type-manhwa",
        LibraryMediaType.LightNovel => "lib-type-lightnovel",
        LibraryMediaType.Webnovel => "lib-type-webnovel",
        _ => "lib-type-manga"
    };

    public string UpdatedLabel
    {
        get
        {
            if (LastReleaseAt is not { } releasedAt)
                return "unknown";

            var span = DateTimeOffset.UtcNow - releasedAt;
            return span.TotalHours switch
            {
                < 1 => "just now",
                < 24 => $"{(int)span.TotalHours}h ago",
                < 24 * 30 => $"{(int)span.TotalDays}d ago",
                _ => $"{(int)(span.TotalDays / 30)}mo ago"
            };
        }
    }
}
