namespace BookmarkManager.Contracts;

public class BackupStatsDto
{
    public BackupManifestDto? LastBackup { get; set; }
    public DateTime? NextScheduledRunUtc { get; set; }
    public int FileCount { get; set; }
    public long DiskUsedBytes { get; set; }
    public long LiveDatabaseSizeBytes { get; set; }
    public double SuccessRate30d { get; set; }
    public int SuccessCount30d { get; set; }
    public int TotalRuns30d { get; set; }
    public IReadOnlyList<BackupActivityDayDto> Activity { get; set; } = [];
    public bool Enabled { get; set; }
    public string ScheduleTime { get; set; } = "03:00";
    public string TimeZoneId { get; set; } = "Europe/Berlin";
    public int RetentionMaxCount { get; set; }
    public int RetentionMaxAgeDays { get; set; }
    public string BackupDirectory { get; set; } = string.Empty;
}
