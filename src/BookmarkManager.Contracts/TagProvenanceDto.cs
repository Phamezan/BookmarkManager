namespace BookmarkManager.Contracts;

public sealed class TagProvenanceDto
{
    public string Tag { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public double? Confidence { get; set; }

    /// <summary>Per-provider title-similarity score (ScoreTokenSets) of the match that supplied this tag. Null when unscored.</summary>
    public double? MatchScore { get; set; }

    /// <summary>Provider-side title the bookmark matched against. Null when unscored.</summary>
    public string? MatchedTitle { get; set; }

    public DateTime CreatedAt { get; set; }
}
