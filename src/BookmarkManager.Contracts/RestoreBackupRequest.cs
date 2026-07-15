namespace BookmarkManager.Contracts;

public sealed class RestoreBackupRequest
{
    public string Confirm { get; set; } = string.Empty;
}
