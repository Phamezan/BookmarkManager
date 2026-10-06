using BookmarkManager.Api.Services.Suwayomi;
using Xunit;

namespace BookmarkManager.UnitTests.Suwayomi;

public sealed class SuwayomiTitleCleanerTests
{
    [Theory]
    [InlineData("Read On My Way to Kill God Manga English [New Chapters] Online Free", "On My Way to Kill God")]
    [InlineData("Read Black Clover", "Black Clover")]
    [InlineData("Solo Leveling", "Solo Leveling")]
    public void Extract_StripsReaderSiteBoilerplate(string title, string expectedSeries)
    {
        var result = SuwayomiTitleCleaner.Extract(title, url: null);

        Assert.Equal(expectedSeries, result.SeriesName);
    }

    [Fact]
    public void Extract_SplitsSeriesAndChapter_FromDelimitedTitleWithSiteSuffix()
    {
        var result = SuwayomiTitleCleaner.Extract(
            "Memoir Of The God Of War - Chapter 67 - WEBTOON XYZ",
            "https://www.webtoon.xyz/read/memoir-of-the-god-of-war/chapter-67/");

        Assert.Equal("Memoir Of The God Of War", result.SeriesName);
        Assert.Equal("67", result.ChapterNumber);
    }

    [Fact]
    public void Extract_EmptyTitle_ReturnsEmptySeriesAndNoChapter()
    {
        var result = SuwayomiTitleCleaner.Extract("", null);

        Assert.Equal(string.Empty, result.SeriesName);
        Assert.Null(result.ChapterNumber);
    }
}
