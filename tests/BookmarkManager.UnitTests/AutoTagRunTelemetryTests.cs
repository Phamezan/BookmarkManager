using BookmarkManager.Api.Services.BookmarkTagging;

namespace BookmarkManager.UnitTests;

public sealed class AutoTagRunTelemetryTests
{
    [Fact]
    public void Record_ConcurrentAdds_DoesNotThrowAndPreservesAllEntries()
    {
        using var telemetry = AutoTagRunTelemetry.BeginScope();

        Parallel.For(0, 100, i => telemetry.Record("MangaUpdates", "search", i, i * 2, cacheHit: false));

        var messages = new List<string>();
        telemetry.AppendSummaryTo(messages);

        Assert.Single(messages, message => message.StartsWith("Provider timing", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("MangaUpdates.search: 100 network", StringComparison.Ordinal));
        Assert.Equal(100, telemetry.SnapshotRecords().Count);
    }

    [Fact]
    public void RecordHttp_TimeSpanOverload_UsesTotalMillisecondsNotComponent()
    {
        using var telemetry = AutoTagRunTelemetry.BeginScope();

        ProviderAutoTagTelemetry.RecordHttp("MangaUpdates", "search", TimeSpan.FromMilliseconds(2500));

        var record = telemetry.SnapshotRecords().Single();
        Assert.Equal(2500, record.HttpMs);
    }
}
