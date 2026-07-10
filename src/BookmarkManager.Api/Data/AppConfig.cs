namespace BookmarkManager.Api.Data;

public static class AppConfigConstants
{
    public const int SingletonId = 1;
    public const int DefaultPollIntervalSeconds = 30;
    public const int DefaultReleaseWatcherIntervalHours = 6;
}

public class AppConfig
{
    public int Id { get; set; }
    public int ConfigVersion { get; set; }
    public int PollIntervalSeconds { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string DisabledProviders { get; set; } = string.Empty;
    public int ReleaseWatcherIntervalHours { get; set; } = AppConfigConstants.DefaultReleaseWatcherIntervalHours;
}
