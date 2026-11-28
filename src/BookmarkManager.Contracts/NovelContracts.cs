namespace BookmarkManager.Contracts;

public enum NovelReadingStatus
{
    Reading,
    Plan,
    Completed,
    Dropped
}

public class NovelSourceDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Site { get; set; } = string.Empty;
}

public class NovelCatalogItemDto
{
    public Guid Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public List<string> AlternateTitles { get; set; } = [];
    public List<string> Authors { get; set; } = [];
    public string MediaType { get; set; } = string.Empty;
    public string? CoverUrl { get; set; }
    public string? Synopsis { get; set; }
    public List<string> Genres { get; set; } = [];
    public double? Rating { get; set; }
    public string? Status { get; set; }
    public string? LatestChapter { get; set; }
    public string? LatestVolume { get; set; }
    public string? SourceUrl { get; set; }
    public int? PopularityRank { get; set; }
    public bool InLibrary { get; set; }
    public bool Readable { get; set; }
    public int? Chapters { get; set; }
    public DateTimeOffset? LastReleaseAt { get; set; }

    /// <summary>"saved", "dismissed" or null.</summary>
    public string? ListState { get; set; }

    /// <summary>For-you only: seed titles (or "Genre: ..." fallback) that led to this recommendation.</summary>
    public List<string> BecauseOf { get; set; } = [];
}

public class NovelGenreCountDto
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class NovelMetaDto
{
    public List<NovelGenreCountDto> Genres { get; set; } = [];
}

public class NovelShelfDto
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string? BrowseQuery { get; set; }
    public List<NovelCatalogItemDto> Items { get; set; } = [];
}

public class NovelListUpdateRequest
{
    /// <summary>"saved", "dismissed" or null (removes the entry).</summary>
    public string? State { get; set; }
    public string? Title { get; set; }
    public string? CoverUrl { get; set; }
}

public class NovelCatalogPageDto
{
    public int Page { get; set; }
    public bool HasNextPage { get; set; }
    public int Total { get; set; }
    public List<NovelCatalogItemDto> Items { get; set; } = [];
}

public class NovelSummaryDto
{
    public string Source { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? Cover { get; set; }
    public bool IsExactMatch { get; set; }
}

public class NovelChapterSummaryDto
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public double? Number { get; set; }
    public string? ReleaseTime { get; set; }
    public bool Locked { get; set; }
}

public class NovelLibraryEntryDto
{
    public Guid Id { get; set; }
    public string Source { get; set; } = string.Empty;
    public string NovelPath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? CoverUrl { get; set; }
    public NovelReadingStatus Status { get; set; } = NovelReadingStatus.Reading;
    public string? LastChapterPath { get; set; }
    public string? LastChapterName { get; set; }
    public int? LastChapterIndex { get; set; }
    public int? ChapterCount { get; set; }
    public double ScrollPercent { get; set; }
    public DateTime AddedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? LastReadAt { get; set; }
}

public class NovelDetailDto
{
    public string Source { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string Site { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? Cover { get; set; }
    public string? Author { get; set; }
    public string Status { get; set; } = "Unknown";
    public List<string> Genres { get; set; } = [];
    public string Summary { get; set; } = string.Empty;
    public List<NovelChapterSummaryDto> Chapters { get; set; } = [];
    public NovelLibraryEntryDto? LibraryEntry { get; set; }
}

public class NovelChapterContentDto
{
    public string Title { get; set; } = string.Empty;
    public string Html { get; set; } = string.Empty;
    public string? PrevPath { get; set; }
    public string? NextPath { get; set; }
    public bool NextLocked { get; set; }
    public string Site { get; set; } = string.Empty;
    public int Index { get; set; }
    public int Count { get; set; }
}

public class UpsertNovelLibraryRequest
{
    public string Source { get; set; } = string.Empty;
    public string NovelPath { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? CoverUrl { get; set; }
    public NovelReadingStatus? Status { get; set; }
}

public class UpdateNovelLibraryStatusRequest
{
    public NovelReadingStatus? Status { get; set; }
}

public class UpdateNovelLibraryProgressRequest
{
    public string ChapterPath { get; set; } = string.Empty;
    public string? ChapterName { get; set; }
    public int ChapterIndex { get; set; }
    public int? ChapterCount { get; set; }
    public double ScrollPercent { get; set; }
}

public class NovelStatsDto
{
    public string Source { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int? Rank { get; set; }
    public double? Rating { get; set; }
    public int? RatingCount { get; set; }
    public long? Views { get; set; }
    public int? Bookmarks { get; set; }
    public int? Chapters { get; set; }
    public string? Status { get; set; }
    public string? LatestChapterName { get; set; }
    public string? LatestChapterUpdated { get; set; }
    public int? ReviewCount { get; set; }
    public double? ReviewAverage { get; set; }
}

public class NovelReviewParagraphDto
{
    public string Text { get; set; } = string.Empty;
    public bool Spoiler { get; set; }
}

public class NovelReviewItemDto
{
    public string Author { get; set; } = string.Empty;
    public string? Date { get; set; }
    public double? Score { get; set; }
    public int? Likes { get; set; }
    public List<NovelReviewParagraphDto> Paragraphs { get; set; } = [];
}

public class NovelReviewsPageDto
{
    public string Source { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public string Site { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public double? Average { get; set; }
    public int? Count { get; set; }
    public int Page { get; set; }
    public bool HasNextPage { get; set; }
    public List<NovelReviewItemDto> Items { get; set; } = [];
}

public class RoyalRoadReviewsPageDto
{
    public bool Matched { get; set; }
    public string? Title { get; set; }
    public string? Path { get; set; }
    public string Site { get; set; } = "https://www.royalroad.com";
    public double? OverallScore { get; set; }
    public int? Ratings { get; set; }
    public long? Views { get; set; }
    public int? Followers { get; set; }
    public int? Favorites { get; set; }
    public int? Count { get; set; }
    public int Page { get; set; }
    public bool HasNextPage { get; set; }
    public List<NovelReviewItemDto> Items { get; set; } = [];
}
