namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>A rolling 7-day window: <see cref="Start"/> exclusive, <see cref="End"/> inclusive.</summary>
public readonly record struct DiscoverWindow(DateTime Start, DateTime End);

/// <summary>
/// Week paging for the Discover feed. Window N ends at <c>now - 7N days</c> and spans the seven days
/// before it, so <c>week=0</c> is <c>(now-7d, now]</c> and <c>week=1</c> is <c>(now-14d, now-7d]</c>.
/// </summary>
public static class DiscoverWindowCalculator
{
    public static DiscoverWindow ForWeek(int week, DateTime nowUtc)
    {
        var clamped = Math.Max(0, week);
        var end = nowUtc.AddDays(-7 * clamped);
        return new DiscoverWindow(end.AddDays(-7), end);
    }

    /// <summary>"NEW" is relative to the last 7 days, independent of which week is being viewed.</summary>
    public static bool IsNew(DateTime uploadedAt, DateTime nowUtc) => uploadedAt > nowUtc.AddDays(-7);
}
