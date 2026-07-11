using System;

namespace BookmarkManager.Contracts;

/// <summary>Diagnostics for the background catalog bulk-import/refresh crawl (see
/// LibraryCatalogSyncBackgroundService), surfaced on the Settings page.</summary>
public sealed class LibraryCatalogSyncStatusDto
{
    public int TotalEntries { get; set; }
    public int PendingQueueCount { get; set; }
    public int ProcessingQueueCount { get; set; }
    public int FailedQueueCount { get; set; }
    public bool IsCrawling { get; set; }
    public DateTimeOffset? LastRefreshedAt { get; set; }
}
