namespace BookmarkManager.Contracts;

public sealed class HtmlBookmarkRestoreResultDto
{
    public Guid SafetyBackupId { get; set; }
    public int RestoredBookmarkCount { get; set; }
    public int RestoredFolderCount { get; set; }
    public int ReplacedNodeCount { get; set; }
    public string Message { get; set; } = string.Empty;
}
