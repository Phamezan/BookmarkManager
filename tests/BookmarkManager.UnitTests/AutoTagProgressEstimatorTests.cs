using BookmarkManager.Client.Services.AutoTagging;

namespace BookmarkManager.UnitTests;

public sealed class AutoTagProgressEstimatorTests
{
    [Fact]
    public void EstimateRemaining_UsesGlobalRemainingAcrossFolders()
    {
        var startedAt = DateTimeOffset.UtcNow.AddSeconds(-20);

        var eta = AutoTagProgressEstimator.EstimateRemaining(startedAt, processedCount: 10, totalCount: 100);

        Assert.NotNull(eta);
        Assert.Contains("remaining", eta, StringComparison.Ordinal);
        Assert.DoesNotContain("finishing", eta, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EstimateRemaining_Warmup_ReturnsEstimating()
    {
        var startedAt = DateTimeOffset.UtcNow;

        var eta = AutoTagProgressEstimator.EstimateRemaining(startedAt, processedCount: 1, totalCount: 10);

        Assert.Equal("estimating...", eta);
    }
}
