namespace BookmarkManager.Contracts;

public sealed class ExtensionStatusDto
{
    public bool IsConnected { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public string? ExtensionVersion { get; set; }
    public string? BraveVersion { get; set; }
}
