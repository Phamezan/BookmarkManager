namespace BookmarkManager.Api.Services.Backup;

public sealed class BackupOptions
{
    public const string SectionName = "Backup";

    public string Directory { get; init; } = "/data/backups/db";
    public bool Enabled { get; init; } = true;
    public string ScheduleTime { get; init; } = "03:00";
    public string TimeZoneId { get; init; } = BackupTimeZones.DefaultTimeZoneId;
    public int RetentionMaxCount { get; init; } = 30;
    public int RetentionMaxAgeDays { get; init; } = 60;
    public long MinFreeDiskBytes { get; init; } = 268_435_456;

    /// <summary>
    /// Whether the host process should stop itself after successfully staging a restore, so
    /// Docker/dotnet run restarts the process and applies the pending snapshot on the next
    /// startup. Disabled in local integration tests since the test host does not restart.
    /// </summary>
    public bool StopHostAfterRestore { get; init; } = true;
}
