using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Backup;
using BookmarkManager.Contracts;

namespace BookmarkManager.UnitTests.Backup;

public sealed class BackupStatsAggregatorTests
{
    [Fact]
    public void BuildActivitySeries_IncludesGapsAndFailures()
    {
        var utcNow = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var manifests = new List<BackupManifest>
        {
            new()
            {
                Name = "bookmarks-2026-07-14-0300.db",
                Status = BackupManifestStatus.Succeeded,
                CreatedAt = utcNow.AddDays(-1),
                SizeBytes = 2_000_000,
                DurationMs = 1200,
                BookmarkCount = 10
            },
            new()
            {
                Name = "bookmarks-2026-07-12-0300.db",
                Status = BackupManifestStatus.Failed,
                CreatedAt = utcNow.AddDays(-3),
                Error = "database_full"
            }
        };

        var activity = BackupStatsAggregator.BuildActivitySeries(manifests, utcNow, "Europe/Berlin");

        Assert.Equal(14, activity.Count);
        Assert.Contains(activity, day => day.Date == DateOnly.FromDateTime(utcNow.AddDays(-1).Date) && day.Status == BackupManifestStatus.Succeeded);
        Assert.Contains(activity, day => day.Date == DateOnly.FromDateTime(utcNow.AddDays(-3).Date) && day.Status == BackupManifestStatus.Failed);
        Assert.Contains(activity, day => day.Status is null);
    }

    [Fact]
    public void ComputeSuccessRate30d_CountsSucceededAndFailedOnly()
    {
        var utcNow = new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc);
        var manifests = new List<BackupManifest>
        {
            new() { Status = BackupManifestStatus.Succeeded, CreatedAt = utcNow.AddDays(-1) },
            new() { Status = BackupManifestStatus.Failed, CreatedAt = utcNow.AddDays(-2) },
            new() { Status = BackupManifestStatus.Running, CreatedAt = utcNow.AddDays(-3) }
        };

        var (rate, successCount, totalRuns) = BackupStatsAggregator.ComputeSuccessRate30d(manifests, utcNow);

        Assert.Equal(50, rate);
        Assert.Equal(1, successCount);
        Assert.Equal(2, totalRuns);
    }
}
