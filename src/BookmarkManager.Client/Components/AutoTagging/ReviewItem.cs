using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Components;

/// <summary>
/// Mutable view-model for one row in the auto-tagger review screen.
/// Kept as a reference type so a sub-component can edit Tags/NewTagText
/// in place without triggering a parent re-render.
/// </summary>
public sealed class ReviewItem
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public List<string> Tags { get; set; } = [];
    public string NewTagText { get; set; } = string.Empty;
    public List<TagScoreDto> TagScores { get; set; } = [];

    // Snapshot of the AI-suggested state, captured when the row is created, so the
    // review screen can tell which rows the user actually touched before saving.
    public string OriginalTitle { get; set; } = string.Empty;
    public List<string> OriginalTags { get; set; } = [];

    /// <summary>Opt-in rename suggestion from batch tagging. Not pre-filled into Title.</summary>
    public string? SuggestedTitle { get; set; }

    public bool HasSuggestion =>
        !string.IsNullOrWhiteSpace(SuggestedTitle)
        && !string.Equals(SuggestedTitle.Trim(), Title.Trim(), StringComparison.OrdinalIgnoreCase);

    public bool TitleChanged => !string.Equals(Title, OriginalTitle, StringComparison.Ordinal);

    public bool TagsChanged =>
        Tags.Count != OriginalTags.Count
        || Tags.Except(OriginalTags, StringComparer.OrdinalIgnoreCase).Any()
        || OriginalTags.Except(Tags, StringComparer.OrdinalIgnoreCase).Any();
}
