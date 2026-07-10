using System;

namespace BookmarkManager.Api.Data;

public class ReleaseEvent
{
    public Guid Id { get; set; }
    public Guid TrackedSeriesId { get; set; }
    public string Chapter { get; set; } = string.Empty;
    public string? Volume { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? Url { get; set; }

    // Navigation property
    public TrackedSeries TrackedSeries { get; set; } = null!;
}
