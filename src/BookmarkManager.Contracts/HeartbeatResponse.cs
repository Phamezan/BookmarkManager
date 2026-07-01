namespace BookmarkManager.Contracts;

public sealed class HeartbeatResponse
{
    public Guid ExtensionClientId { get; set; }
    public DateTime ServerTime { get; set; }
    public int ConfigVersion { get; set; }
    public int PollIntervalSeconds { get; set; }
}
