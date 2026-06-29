namespace BookmarkManager.Contracts;

public class BackupManifestDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public int BookmarkCount { get; set; }
    public long SizeBytes { get; set; }
}
