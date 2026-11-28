using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Data;

public class NovelLibraryEntry
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
