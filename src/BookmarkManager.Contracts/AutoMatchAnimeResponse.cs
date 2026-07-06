namespace BookmarkManager.Contracts;

public class AutoMatchAnimeResponse
{
    public List<AutoMatchAnimeEntryDto> Matched { get; set; } = [];
    public List<AutoMatchAnimeEntryDto> Skipped { get; set; } = [];
    public int SkippedCooldownCount { get; set; }
    public bool AniListUnavailable { get; set; }
}
