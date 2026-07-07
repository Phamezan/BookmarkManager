namespace BookmarkManager.Contracts;

public class AnimeCalendarScheduleResponse
{
    public List<AnimeCalendarEntryDto> Entries { get; set; } = [];
    public List<BookmarkNodeDto> UnmatchedBookmarks { get; set; } = [];
    public int AiringCount { get; set; }
    public int FinishedCount { get; set; }
    public bool AniListDegraded { get; set; }
}
