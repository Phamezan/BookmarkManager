using BookmarkManager.Api.Services.Library;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

public sealed class ProviderCircuitBreakerTests
{
    [Fact]
    public void IsOpen_FalseUntilFailureThresholdReached()
    {
        var breaker = new ProviderCircuitBreaker(failureThreshold: 3, cooldown: TimeSpan.FromMinutes(1));

        breaker.RecordFailure();
        Assert.False(breaker.IsOpen);
        breaker.RecordFailure();
        Assert.False(breaker.IsOpen);
        breaker.RecordFailure();
        Assert.True(breaker.IsOpen);
    }

    [Fact]
    public void RecordSuccess_ResetsFailureCountAndClosesBreaker()
    {
        var breaker = new ProviderCircuitBreaker(failureThreshold: 2, cooldown: TimeSpan.FromMinutes(1));

        breaker.RecordFailure();
        breaker.RecordFailure();
        Assert.True(breaker.IsOpen);

        breaker.RecordSuccess();
        Assert.False(breaker.IsOpen);
    }

    [Fact]
    public void IsOpen_ClosesAfterCooldownElapses()
    {
        var breaker = new ProviderCircuitBreaker(failureThreshold: 1, cooldown: TimeSpan.FromMilliseconds(50));

        breaker.RecordFailure();
        Assert.True(breaker.IsOpen);

        Thread.Sleep(100);
        Assert.False(breaker.IsOpen);
    }
}
