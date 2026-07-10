using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Features.Library;

/// <summary>
/// One catalog entry in the Library — wraps a provider search result. Immutable, so updates
/// produce a new record via <c>with</c>.
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
    IReadOnlyList<string>? AlternateTitles = null,
    bool IsTrending = false)
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
        dto.AlternateTitles,
        isTrending);

    public string Author => Authors.Count > 0 ? string.Join(", ", Authors) : "Unknown";

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
