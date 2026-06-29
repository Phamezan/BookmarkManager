namespace BookmarkManager.Api.Data;

public class BackupManifest
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int BookmarkCount { get; set; }
    public long SizeBytes { get; set; }
    public string? FilePath { get; set; }
}
