namespace BookmarkManager.Client.Services.AutoTagging;

public static class AutoTagProgressEstimator
{
    public static readonly TimeSpan WarmupDuration = TimeSpan.FromSeconds(2);

    public static string? EstimateRemaining(
        DateTimeOffset startedAt,
        int processedCount,
        int totalCount)
    {
        var remaining = Math.Max(0, totalCount - processedCount);
        if (processedCount <= 0)
            return remaining <= 0 ? "finishing..." : "estimating...";

        if (remaining <= 0)
            return "finishing...";

        var elapsed = DateTimeOffset.UtcNow - startedAt;
        if (elapsed < WarmupDuration)
            return "estimating...";

        var secondsPerBookmark = elapsed.TotalSeconds / processedCount;
        var etaSeconds = secondsPerBookmark * remaining;
        return $"~{FormatDuration(TimeSpan.FromSeconds(etaSeconds))} remaining";
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        return $"{Math.Max(1, (int)Math.Ceiling(duration.TotalSeconds))}s";
    }
}
