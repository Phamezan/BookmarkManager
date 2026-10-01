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

        return ToUtcResolvingDst(localNext, timeZone);
    }

    public static DateTimeOffset ToTimeZone(DateTime utc, string timeZoneId)
    {
        var timeZone = ResolveTimeZone(timeZoneId);
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(utc, TimeSpan.Zero), timeZone);
    }

    /// <summary>Converts an unspecified local wall-clock time to UTC, handling DST transitions that make
    /// the configured schedule time problematic:
    /// <list type="bullet">
    /// <item>a time inside a spring-forward gap (it never occurs) resolves to the first valid instant
    /// after the gap;</item>
    /// <item>a time that occurs twice on a fall-back day resolves to the first occurrence (the one with
    /// the larger UTC offset, i.e. the earlier UTC instant).</item>
    /// </list>
    /// Plain <see cref="TimeZoneInfo.ConvertTimeToUtc(DateTime, TimeZoneInfo)"/> throws
    /// <see cref="ArgumentException"/> for the gap case, which used to escape the background workers and
    /// stop the host.</summary>
    internal static DateTime ToUtcResolvingDst(DateTime local, TimeZoneInfo timeZone)
    {
        if (timeZone.IsInvalidTime(local))
        {
            // The wall-clock time does not exist (spring-forward). Advance minute-by-minute to the first
            // valid instant after the gap; gaps are at most a few hours, well inside this bound.
            var probe = local;
            for (var minutes = 0; minutes < 24 * 60 && timeZone.IsInvalidTime(probe); minutes++)
            {
                probe = probe.AddMinutes(1);
            }

            return ToUtcResolvingDst(probe, timeZone);
        }

        if (timeZone.IsAmbiguousTime(local))
        {
            // Two UTC instants map to this local time. The first occurrence is the one with the larger
            // offset (e.g. +02:00 before falling back to +01:00).
            var firstOffset = timeZone.GetAmbiguousTimeOffsets(local).Max();
            return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), firstOffset).UtcDateTime;
        }

        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), timeZone);
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
        => BackupTimeZones.Resolve(timeZoneId);
}
