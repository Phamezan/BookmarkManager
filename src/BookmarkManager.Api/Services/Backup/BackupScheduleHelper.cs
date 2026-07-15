using BookmarkManager.Api.Data;

namespace BookmarkManager.Api.Services.Backup;

public static class BackupScheduleHelper
{
    public static DateTime GetNextScheduledRunUtc(DateTime utcNow, string scheduleTime, string timeZoneId)
    {
        var timeZone = ResolveTimeZone(timeZoneId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, timeZone);
        if (!TimeOnly.TryParse(scheduleTime, out var scheduledTime))
        {
            scheduledTime = new TimeOnly(3, 0);
        }

        var localNext = localNow.Date.Add(scheduledTime.ToTimeSpan());
        if (localNext <= localNow)
        {
            localNext = localNext.AddDays(1);
        }

        return TimeZoneInfo.ConvertTimeToUtc(localNext, timeZone);
    }

    public static DateTimeOffset ToTimeZone(DateTime utc, string timeZoneId)
    {
        var timeZone = ResolveTimeZone(timeZoneId);
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(utc, TimeSpan.Zero), timeZone);
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
        => BackupTimeZones.Resolve(timeZoneId);
}
