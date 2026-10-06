using BookmarkManager.Api.Services.Suwayomi;

namespace BookmarkManager.UnitTests.Discover;

public sealed class DiscoverWindowCalculatorTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void WeekZero_SpansTheLastSevenDays()
    {
        var window = DiscoverWindowCalculator.ForWeek(0, Now);

        Assert.Equal(Now.AddDays(-7), window.Start);
        Assert.Equal(Now, window.End);
    }

    [Fact]
    public void WeekOne_EndsSevenDaysAgo()
    {
        var window = DiscoverWindowCalculator.ForWeek(1, Now);

        Assert.Equal(Now.AddDays(-14), window.Start);
        Assert.Equal(Now.AddDays(-7), window.End);
    }

    [Fact]
    public void NegativeWeek_IsClampedToZero()
    {
        var window = DiscoverWindowCalculator.ForWeek(-3, Now);

        Assert.Equal(Now.AddDays(-7), window.Start);
        Assert.Equal(Now, window.End);
    }

    [Fact]
    public void IsNew_IsRelativeToTheLastSevenDays()
    {
        Assert.True(DiscoverWindowCalculator.IsNew(Now.AddDays(-3), Now));
        Assert.True(DiscoverWindowCalculator.IsNew(Now.AddHours(-1), Now));
        Assert.False(DiscoverWindowCalculator.IsNew(Now.AddDays(-7), Now));
        Assert.False(DiscoverWindowCalculator.IsNew(Now.AddDays(-8), Now));
    }
}
