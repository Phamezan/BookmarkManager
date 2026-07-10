using System;

namespace BookmarkManager.Contracts;

public sealed class ReleaseEventDto
{
    public Guid Id { get; set; }
    public string Chapter { get; set; } = string.Empty;
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? Url { get; set; }
}
