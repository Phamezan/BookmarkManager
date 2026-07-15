namespace BookmarkManager.Contracts;

public class BackupActivityDayDto
{
    public DateOnly Date { get; set; }
    public string? Status { get; set; }
    public string? Name { get; set; }
    public long SizeBytes { get; set; }
    public long DurationMs { get; set; }
    public int BookmarkCount { get; set; }
    public string? Error { get; set; }
}
