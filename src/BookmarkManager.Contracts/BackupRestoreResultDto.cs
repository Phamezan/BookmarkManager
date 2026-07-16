namespace BookmarkManager.Contracts;

public sealed class BackupRestoreResultDto
{
    public Guid RestoredBackupId { get; set; }
    public Guid? PreRestoreBackupId { get; set; }
    public bool RestartRequired { get; set; }
    public string Message { get; set; } = string.Empty;
}
