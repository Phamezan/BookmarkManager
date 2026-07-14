namespace BookmarkManager.Client.Services.AutoTagging;

public static class AutoTaggerUiTiming
{
    public const int MaxUiBatchSize = 10;
    public static readonly TimeSpan BatchHeartbeatInterval = TimeSpan.FromSeconds(1);
}
