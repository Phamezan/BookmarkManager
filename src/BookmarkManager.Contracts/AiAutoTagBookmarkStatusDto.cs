namespace BookmarkManager.Contracts;

public sealed class AiAutoTagBookmarkStatusDto
{
    public Guid BookmarkId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? Reason { get; set; }
    public string? Tags { get; set; }

    /// <summary>
    /// Opt-in rename suggestion only — never applied server-side during AI tagging.
    /// Surfaced for display when a review/confirm UI exists; Results & Reruns shows it read-only.
    /// </summary>
    public string? SuggestedTitle { get; set; }
}
