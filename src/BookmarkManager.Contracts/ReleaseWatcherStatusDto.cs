using System;

namespace BookmarkManager.Contracts;

public sealed class ReleaseWatcherStatusDto
{
    public bool IsRunning { get; set; }
    public DateTimeOffset? LastRunTime { get; set; }
    public int TotalTrackedCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailureCount { get; set; }
    public string? LastError { get; set; }
}
