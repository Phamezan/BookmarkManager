using BookmarkManager.Api.Services.UrlMigration;
using Xunit;

namespace BookmarkManager.UnitTests.UrlMigration;

public sealed class SeriesExtractionFallbackTests
{
    [Fact]
    public void Extract_ChapterInUrlPath_ReturnsChapterFromPath()
    {
        var result = SeriesExtractionFallback.Extract(
            "Solo Leveling - Chapter 110 | FlameComics",
            "https://flamecomics.xyz/solo-leveling/chapter-110",
            category: null);

        Assert.Equal("110", result.ChapterNumber);
        Assert.True(result.UsedFallback);
        Assert.Equal("unknown", result.MediaType);
        Assert.Contains("Solo Leveling", result.SeriesName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extract_ChapterOnlyInTitle_FallsBackToTitleRegex()
    {
        var result = SeriesExtractionFallback.Extract(
            "Solo Leveling Chapter 42",
            "https://flamecomics.xyz/series/solo-leveling",
            category: null);

        Assert.Equal("42", result.ChapterNumber);
    }

    [Fact]
    public void Extract_DecimalChapterInPath_ReturnsDecimalString()
    {
        var result = SeriesExtractionFallback.Extract(
            "Omniscient Reader - Chapter 112.5",
            "https://reaperscans.to/omniscient-reader/chapter-112.5",
            category: null);

        Assert.Equal("112.5", result.ChapterNumber);
    }

    [Fact]
    public void Extract_DecimalChapterInTitleOnly_ReturnsDecimalString()
    {
        var result = SeriesExtractionFallback.Extract(
            "Omniscient Reader ch. 112.5",
            "https://reaperscans.to/series/omniscient-reader",
            category: null);

        Assert.Equal("112.5", result.ChapterNumber);
    }

    [Fact]
    public void Extract_NoChapterPresent_ReturnsNull()
    {
        var result = SeriesExtractionFallback.Extract(
            "Solo Leveling - Series Overview",
            "https://flamecomics.xyz/series/solo-leveling",
            category: null);

        Assert.Null(result.ChapterNumber);
    }

    [Fact]
    public void Extract_VolumeAndChapterCombo_ReturnsChapterNumberIgnoringVolume()
    {
        var result = SeriesExtractionFallback.Extract(
            "Return of the Mount Hua Sect - vol 3 ch 12",
            "https://asuracomic.net/series/return-of-the-mount-hua-sect/vol-3/ch-12",
            category: null);

        Assert.Equal("12", result.ChapterNumber);
    }

    [Fact]
    public void Extract_AnimeEpisodeWordingInPath_ReturnsEpisodeNumber()
    {
        var result = SeriesExtractionFallback.Extract(
            "Watch Solo Leveling Episode 5 English Subbed",
            "https://hianime.to/watch/solo-leveling/episode-5",
            category: "Anime");

        Assert.Equal("5", result.ChapterNumber);
    }

    [Fact]
    public void Extract_AnimeEpisodeAbbreviationInTitle_ReturnsEpisodeNumber()
    {
        var result = SeriesExtractionFallback.Extract(
            "Solo Leveling ep-5",
            "https://hianime.to/watch/solo-leveling",
            category: "Anime");

        Assert.Equal("5", result.ChapterNumber);
    }

    [Fact]
    public void Extract_PathTakesPrecedenceOverTitle_WhenBothPresent()
    {
        var result = SeriesExtractionFallback.Extract(
            "Solo Leveling Chapter 1 (old bookmark title)",
            "https://flamecomics.xyz/solo-leveling/chapter-110",
            category: null);

        Assert.Equal("110", result.ChapterNumber);
    }

    [Fact]
    public void Extract_AlwaysMarksUsedFallbackTrue()
    {
        var result = SeriesExtractionFallback.Extract("Some Series", "https://example.com/some-series", null);
        Assert.True(result.UsedFallback);
    }
}
