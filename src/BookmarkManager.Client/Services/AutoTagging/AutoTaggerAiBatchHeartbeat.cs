namespace BookmarkManager.Client.Services.AutoTagging;

public static class AutoTaggerAiBatchHeartbeat
{
    public static async Task<T> WaitForCompletionAsync<T>(
        Task<T> requestTask,
        DateTimeOffset startedAt,
        TimeSpan tickInterval,
        Func<TimeSpan, Task> onTickAsync,
        CancellationToken cancellationToken = default)
    {
        using var heartbeat = new PeriodicTimer(tickInterval);
        while (!requestTask.IsCompleted)
        {
            if (!await heartbeat.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                break;

            await onTickAsync(DateTimeOffset.UtcNow - startedAt).ConfigureAwait(false);
        }

        return await requestTask.ConfigureAwait(false);
    }
}
