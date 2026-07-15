namespace BookmarkManager.Api.Services.Backup;

public static class BackupTimeZones
{
    public const string DefaultTimeZoneId = "Europe/Berlin";

    public static TimeZoneInfo Resolve(string? timeZoneId)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.FindSystemTimeZoneById(DefaultTimeZoneId);
    }
}
