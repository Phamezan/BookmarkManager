namespace BookmarkManager.Contracts;

public sealed class ExtensionConfigDto
{
    public int ConfigVersion { get; set; }
    public int PollIntervalSeconds { get; set; }
    public SnapshotRequestDto? SnapshotRequest { get; set; }
}
