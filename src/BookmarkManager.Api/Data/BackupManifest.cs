namespace BookmarkManager.Api.Data;

public class BackupManifest
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string Status { get; set; } = BackupManifestStatus.Running;
    public string Trigger { get; set; } = BackupManifestTrigger.Manual;
    public long SizeBytes { get; set; }
    public long DurationMs { get; set; }
    public string? Error { get; set; }
    public string? FilePath { get; set; }
    public int BookmarkCount { get; set; }
    public int FolderCount { get; set; }
    public int TagCount { get; set; }
    public int LibraryTitleCount { get; set; }
}

public static class BackupManifestStatus
{
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

public static class BackupManifestTrigger
{
    public const string Scheduled = "Scheduled";
    public const string Manual = "Manual";
    public const string PreRestore = "PreRestore";
}
