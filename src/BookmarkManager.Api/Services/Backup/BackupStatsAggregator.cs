using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.Backup;

public static class BackupStatsAggregator
{
    public static IReadOnlyList<BackupActivityDayDto> BuildActivitySeries(
        IReadOnlyList<BackupManifest> manifests,
        DateTime utcNow,
        string timeZoneId,
        int dayCount = 14)
    {
        var timeZone = ResolveTimeZone(timeZoneId);
        var localToday = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone).Date;
        var days = Enumerable.Range(0, dayCount)
            .Select(offset => DateOnly.FromDateTime(localToday.AddDays(-(dayCount - 1 - offset))))
            .ToList();

        var runsByDay = manifests
            .Where(m => m.Status is BackupManifestStatus.Succeeded or BackupManifestStatus.Failed)
            .GroupBy(m => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(m.CreatedAt, timeZone).Date))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CreatedAt).First());

        return days.Select(day =>
        {
            if (!runsByDay.TryGetValue(day, out var manifest))
            {
                return new BackupActivityDayDto { Date = day };
            }

            return new BackupActivityDayDto
            {
                Date = day,
                Status = manifest.Status,
                Name = manifest.Name,
                SizeBytes = manifest.SizeBytes,
                DurationMs = manifest.DurationMs,
                BookmarkCount = manifest.BookmarkCount,
                Error = manifest.Error
            };
        }).ToList();
    }

    public static (double Rate, int SuccessCount, int TotalRuns) ComputeSuccessRate30d(
        IReadOnlyList<BackupManifest> manifests,
        DateTime utcNow)
    {
        var cutoff = utcNow.AddDays(-30);
        var recent = manifests
            .Where(m => m.CreatedAt >= cutoff
                        && m.Status is BackupManifestStatus.Succeeded or BackupManifestStatus.Failed)
            .ToList();

        if (recent.Count == 0)
        {
            return (0, 0, 0);
        }

        var successCount = recent.Count(m => m.Status == BackupManifestStatus.Succeeded);
        return (100.0 * successCount / recent.Count, successCount, recent.Count);
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
        => BackupTimeZones.Resolve(timeZoneId);
}
