using BookmarkManager.Client.Services.AutoTagging;

namespace BookmarkManager.UnitTests;

public sealed class AiAutoTagSummaryMessageFilterTests
{
    [Theory]
    [InlineData("Provider timing (12.3s total):", false)]
    [InlineData("  MangaUpdates.search: 1 network, 0 cache — limiter 0ms, http 10ms", true)]
    [InlineData("Deterministic pass: processing 3 obvious candidate(s) without AI.", false)]
    [InlineData("  ✓ 'Solo Leveling' tagged: [Novel, Fantasy]", true)]
    public void ShouldDisplay_FiltersKnownServerPrefixes(string message, bool expected)
        => Assert.Equal(expected, AiAutoTagSummaryMessageFilter.ShouldDisplay(message));
}
