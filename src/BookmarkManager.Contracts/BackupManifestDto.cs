namespace BookmarkManager.Contracts;

public class BackupManifestDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Trigger { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public long DurationMs { get; set; }
    public string? Error { get; set; }
    public int BookmarkCount { get; set; }
    public int FolderCount { get; set; }
    public int TagCount { get; set; }
    public int LibraryTitleCount { get; set; }
}
