using System;

namespace BookmarkManager.Contracts;

public sealed record ProviderSyncStatusDto(
    string ProviderName,
    bool IsActive,
    string? CurrentPageToken,
    int TotalEntries,
    DateTimeOffset? LastFetchAt);

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
    public IReadOnlyList<ProviderSyncStatusDto> ProviderStatuses { get; set; } = Array.Empty<ProviderSyncStatusDto>();
}
