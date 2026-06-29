namespace BookmarkManager.Api.Data;

public class ExtensionClient
{
    public Guid Id { get; set; }

    public DateTime FirstSeenAt { get; set; }
    public DateTime LastHeartbeatAt { get; set; }
    public string? ExtensionVersion { get; set; }
    public string? BraveVersion { get; set; }
    public int LocalConfigVersion { get; set; }
    public int PendingEventCount { get; set; }
    public DateTime? LastSuccessfulSyncAt { get; set; }
}
