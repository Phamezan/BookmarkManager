namespace BookmarkManager.Contracts;

public class ConfirmAnimeMatchRequest
{
    public Guid BookmarkId { get; set; }
    public string Source { get; set; } = "AniList";
    public int? AniListId { get; set; }
    public string? Status { get; set; }
}
