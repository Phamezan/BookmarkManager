namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>Suwayomi GraphQL filter node kinds used by the Discover feed.</summary>
public enum SuwayomiFilterKind
{
    Other = 0,
    Sort = 1,
    Select = 2,
    Group = 3,
    CheckBox = 4,
    TriState = 5
}

/// <summary>
/// One node of a source's filter tree. <see cref="Position"/> is the node's index within its
/// containing list (top-level filters, or a group's children), which is what Suwayomi's
/// FilterChangeInput addresses. <see cref="Values"/> holds the selectable names for Sort/Select
/// nodes; <see cref="Filters"/> holds child nodes for a group.
/// </summary>
public sealed record SuwayomiFilter(
    int Position,
    string Name,
    SuwayomiFilterKind Kind,
    IReadOnlyList<string> Values,
    IReadOnlyList<SuwayomiFilter> Filters);

/// <summary>Suwayomi <c>SortStateInput</c>: an index into the sort filter's values plus direction.</summary>
public sealed record SuwayomiSortState(int Index, bool Ascending);

/// <summary>Suwayomi <c>GroupChangeInput</c>: a child index within the group plus its new state.</summary>
public sealed record SuwayomiGroupChange(int Position, bool? CheckBoxState = null, string? TriState = null);

/// <summary>
/// One <c>FilterChangeInput</c> entry. Exactly one of <see cref="SortState"/>, <see cref="SelectState"/>
/// or <see cref="GroupChange"/> is set; the client serializes only the non-null shape (a SortFilter
/// change must use sortState, a selectState throws server-side).
/// </summary>
public sealed record SuwayomiFilterChange(
    int Position,
    SuwayomiSortState? SortState = null,
    int? SelectState = null,
    SuwayomiGroupChange? GroupChange = null);

/// <summary>A listing entry returned by a filtered fetchSourceManga page.</summary>
public sealed record SuwayomiSourceManga(int Id, string Title);

/// <summary>One filtered listing page.</summary>
public sealed record SuwayomiSourcePage(bool HasNextPage, IReadOnlyList<SuwayomiSourceManga> Mangas);

/// <summary>A chapter as needed by Discover (name, upload time, reader order).</summary>
public sealed record SuwayomiChapterDetails(
    int Id, string Name, double ChapterNumber, DateTime? UploadedAt, int SourceOrder);

/// <summary>Manga metadata plus chapters needed to build the Discover feed.</summary>
public sealed record SuwayomiMangaDetails(
    string Title,
    IReadOnlyList<string> Genres,
    string? Status,
    string? ThumbnailUrl,
    IReadOnlyList<SuwayomiChapterDetails> Chapters);

/// <summary>A library series' reading progress, used for the Discover progress badge.</summary>
public sealed record SuwayomiLibraryManga(string Title, double? LatestReadChapter, double? HighestNumberedChapter);
