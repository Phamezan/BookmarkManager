namespace BookmarkManager.Contracts;

public sealed class HeartbeatRequest
{
    public string ExtensionVersion { get; set; } = string.Empty;
    public string BraveVersion { get; set; } = string.Empty;
    public int LocalConfigVersion { get; set; }
    public int PendingEventCount { get; set; }
    public DateTime? LastSuccessfulSyncAt { get; set; }
}
