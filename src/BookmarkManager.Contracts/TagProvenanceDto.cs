namespace BookmarkManager.Contracts;

public sealed class TagProvenanceDto
{
    public string Tag { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public double? Confidence { get; set; }
    public DateTime CreatedAt { get; set; }
}
