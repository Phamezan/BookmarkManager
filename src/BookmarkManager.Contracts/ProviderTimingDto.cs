namespace BookmarkManager.Contracts;

public sealed class ProviderTimingDto
{
    public string Provider { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public int NetworkCalls { get; set; }
    public int CacheHits { get; set; }
    public long LimiterMs { get; set; }
    public long HttpMs { get; set; }
}
