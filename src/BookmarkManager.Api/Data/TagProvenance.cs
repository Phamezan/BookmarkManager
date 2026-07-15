namespace BookmarkManager.Api.Data;

public class TagProvenance
{
    public int Id { get; set; }
    public Guid BookmarkId { get; set; }
    public string Tag { get; set; } = string.Empty;

    /// <summary>Source that supplied the tag (e.g. AniList, Kitsu, DomainRoute, Manual).</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// AI series-identification confidence for the run that produced this tag —
    /// not a per-provider score. Null for deterministic and manual writes.
    /// </summary>
    public double? Confidence { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
