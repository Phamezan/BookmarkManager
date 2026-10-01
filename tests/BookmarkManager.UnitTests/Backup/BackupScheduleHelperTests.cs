using BookmarkManager.Api.Services.Backup;

namespace BookmarkManager.UnitTests.Backup;

public sealed class BackupScheduleHelperTests
{
    [Fact]
    public void GetNextScheduledRunUtc_UsesEuropeBerlinLocalTime()
    {
        var utcNow = new DateTime(2026, 7, 15, 0, 30, 0, DateTimeKind.Utc);
        var next = BackupScheduleHelper.GetNextScheduledRunUtc(utcNow, "03:00", "Europe/Berlin");
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var local = TimeZoneInfo.ConvertTimeFromUtc(next, berlin);

        Assert.Equal(new TimeOnly(3, 0), TimeOnly.FromDateTime(local));
        Assert.Equal(new DateOnly(2026, 7, 15), DateOnly.FromDateTime(local));
    }

    [Fact]
    public void GetNextScheduledRunUtc_RollsToNextDayAfterScheduledTime()
    {
        var utcNow = new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc);
        var next = BackupScheduleHelper.GetNextScheduledRunUtc(utcNow, "03:00", "Europe/Berlin");
        var berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var local = TimeZoneInfo.ConvertTimeFromUtc(next, berlin);

        Assert.Equal(new DateOnly(2026, 7, 16), DateOnly.FromDateTime(local));
    }

    [Fact]
    public void GetNextScheduledRunUtc_SpringForwardGap_ResolvesToFirstValidInstant()
    {
        // Berlin springs forward on 2026-03-29 at 02:00 -> 03:00, so 02:30 does not exist. From 05:00
        // local on 03-28 the next 02:30 is the nonexistent 03-29 02:30, which must resolve to 03:00
        // local (CEST, UTC+2) = 01:00 UTC instead of throwing.
        var utcNow = new DateTime(2026, 3, 28, 4, 0, 0, DateTimeKind.Utc);

        var next = BackupScheduleHelper.GetNextScheduledRunUtc(utcNow, "02:30", "Europe/Berlin");

        Assert.Equal(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc), next);
    }

    [Fact]
    public void GetNextScheduledRunUtc_FallBackAmbiguousTime_PicksFirstOccurrence()
    {
        // Berlin falls back on 2026-10-25 at 03:00 -> 02:00, so 02:30 happens twice. The first
        // occurrence is the CEST one (UTC+2) = 00:30 UTC.
        var utcNow = new DateTime(2026, 10, 24, 4, 0, 0, DateTimeKind.Utc);

        var next = BackupScheduleHelper.GetNextScheduledRunUtc(utcNow, "02:30", "Europe/Berlin");

        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), next);
    }
}
