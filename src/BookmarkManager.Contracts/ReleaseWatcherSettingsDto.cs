using System.ComponentModel.DataAnnotations;

namespace BookmarkManager.Contracts;

public sealed class ReleaseWatcherSettingsDto
{
    [Range(1, 168)]
    public int IntervalHours { get; set; } = 6;
}
