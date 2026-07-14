namespace BookmarkManager.Api.Services.BookmarkTagging;

internal static class ProviderAutoTagTelemetry
{
    public static void RecordCacheHit(string provider, string operation = "lookup")
        => AutoTagRunTelemetry.TryGetCurrent()?.Record(provider, operation, 0, 0, cacheHit: true);

    public static void RecordHttp(
        string provider,
        string operation,
        long httpMs,
        long limiterWaitMs = 0)
        => AutoTagRunTelemetry.TryGetCurrent()?.Record(provider, operation, limiterWaitMs, httpMs, cacheHit: false);

    public static void RecordHttp(
        string provider,
        string operation,
        TimeSpan httpDuration,
        TimeSpan? limiterWait = null)
        => RecordHttp(
            provider,
            operation,
            (long)httpDuration.TotalMilliseconds,
            (long)(limiterWait?.TotalMilliseconds ?? 0));

    public static void RecordFailure(string provider, string operation)
        => RecordHttp(provider, operation, httpMs: 0);
}
