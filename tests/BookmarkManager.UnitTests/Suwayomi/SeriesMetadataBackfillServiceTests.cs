using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.Suwayomi;

namespace BookmarkManager.UnitTests.Suwayomi;

public sealed class SeriesMetadataBackfillServiceTests
{
    private static readonly DateTime Base = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);

    private static DiscoverSeries Series(
        string title, DateTime latestChapterAt, DiscoverSeriesType type = DiscoverSeriesType.Unknown)
        => new()
        {
            Id = Guid.NewGuid(),
            TitleKey = MediaTitleNormalizer.NormalizeForSearch(title),
            Title = title,
            Type = type,
            LatestChapterAt = latestChapterAt
        };

    [Fact]
    public void SelectCandidates_OrdersLibraryFirstThenDiscoverByLatestChapterDescending()
    {
        var library = new[] { "Solo Leveling", "Omniscient Reader" };
        DiscoverSeries[] discover =
        [
            Series("Mid", Base.AddDays(-5)),
            Series("Recent", Base.AddDays(-1)),
            Series("Old", Base.AddDays(-20))
        ];

        var pending = SeriesMetadataBackfillService.SelectCandidates(
            library, discover, new HashSet<string>(), 100);

        Assert.Equal(
            new[] { "Solo Leveling", "Omniscient Reader", "Recent", "Mid", "Old" },
            pending.Select(p => p.Title));
    }

    [Fact]
    public void SelectCandidates_IncludesEveryDiscoverSeriesRegardlessOfType()
    {
        DiscoverSeries[] discover =
        [
            Series("An Unknown", Base.AddDays(-1), DiscoverSeriesType.Unknown),
            Series("A Manhwa", Base.AddDays(-2), DiscoverSeriesType.Manhwa),
            Series("A Manhua", Base.AddDays(-3), DiscoverSeriesType.Manhua),
            Series("A Manga", Base.AddDays(-4), DiscoverSeriesType.Manga)
        ];

        var pending = SeriesMetadataBackfillService.SelectCandidates(
            [], discover, new HashSet<string>(), 100);

        Assert.Equal(4, pending.Count);
    }

    [Fact]
    public void SelectCandidates_SkipsFreshKeys()
    {
        DiscoverSeries[] discover =
        [
            Series("Fresh", Base.AddDays(-1)),
            Series("Stale", Base.AddDays(-2))
        ];
        var fresh = new HashSet<string>(StringComparer.Ordinal)
        {
            MediaTitleNormalizer.NormalizeForSearch("Fresh")
        };

        var pending = SeriesMetadataBackfillService.SelectCandidates([], discover, fresh, 100);

        Assert.Equal(new[] { "Stale" }, pending.Select(p => p.Title));
    }

    [Fact]
    public void SelectCandidates_DeduplicatesByKeyAndLibraryWins()
    {
        var library = new[] { "Solo Leveling" };
        DiscoverSeries[] discover = [Series("solo leveling", Base.AddDays(-1))];

        var pending = SeriesMetadataBackfillService.SelectCandidates(
            library, discover, new HashSet<string>(), 100);

        var single = Assert.Single(pending);
        Assert.Equal("Solo Leveling", single.Title);
    }

    [Fact]
    public void SelectCandidates_CapsAtMax()
    {
        DiscoverSeries[] discover =
        [
            Series("A", Base.AddDays(-1)),
            Series("B", Base.AddDays(-2)),
            Series("C", Base.AddDays(-3))
        ];

        var pending = SeriesMetadataBackfillService.SelectCandidates([], discover, new HashSet<string>(), 2);

        Assert.Equal(new[] { "A", "B" }, pending.Select(p => p.Title));
    }

    [Fact]
    public void SelectCandidates_DropsEmptyNormalizedKey()
    {
        DiscoverSeries[] discover = [Series("!!!", Base.AddDays(-1)), Series("Keep Me", Base.AddDays(-2))];

        var pending = SeriesMetadataBackfillService.SelectCandidates([], discover, new HashSet<string>(), 100);

        Assert.Equal(new[] { "Keep Me" }, pending.Select(p => p.Title));
    }
}
