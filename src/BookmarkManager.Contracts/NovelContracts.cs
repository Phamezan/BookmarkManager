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
