namespace BookmarkManager.Contracts;

public class SyncStatusDto
{
    public int TotalNodeCount { get; set; }
    public int PendingSyncCount { get; set; }
    public DateTime LastSyncAt { get; set; }
}
