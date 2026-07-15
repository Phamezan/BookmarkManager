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
}
