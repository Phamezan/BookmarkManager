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

    [Fact]
    public void Extract_DoesNotMatchChapterMarkerEmbeddedInWord()
    {
        var result = SeriesExtractionFallback.Extract(
            "Research Paper",
            "https://example.com/research42",
            category: null);

        Assert.Null(result.ChapterNumber);
    }

    [Fact]
    public void Extract_StreamingSlug_PreservesAcronymAndRomanNumeralCasing()
    {
        var result = SeriesExtractionFallback.Extract(
            "Watch junk title",
            "https://hianime.to/watch/sss-class-suicide-hunter-iii-yqqv0",
            category: "Anime");

        Assert.Equal("SSS Class Suicide Hunter III", result.SeriesName);
    }

    [Theory]
    [InlineData("https://www.webtoon.xyz/read/nano-machine/chapter-330/", "nano-machine", "330")]
    [InlineData("https://www.webtoon.xyz/manga/murim-psychopath/chapter-44/", "murim-psychopath", "44")]
    [InlineData("https://comizy.io/nano-machine/chapter-330", "nano-machine", "330")]
    [InlineData("https://www.webtoon.xyz/read/how-to-live-as-a-villain/chapter-37", "how-to-live-as-a-villain", "37")]
    [InlineData("https://www.webtoon.xyz/read/omniscient-reader/chapter-112.5/", "omniscient-reader", "112.5")]
    [InlineData("https://site.example/read/return-of-the-mount-hua-sect/chapter-180/page-2", "return-of-the-mount-hua-sect", "180")]
    [InlineData("https://site.example/series/the-former-supreme/chapter/16", "the-former-supreme", "16")]
    [InlineData("https://site.example/read/mob-psycho-100/chapter-5/", "mob-psycho-100", "5")]
    public void TryParseSeriesSlugAndChapter_CommonShapes_ReturnsSlugAndChapter(
        string url, string expectedSlug, string expectedChapter)
    {
        var parsed = SeriesExtractionFallback.TryParseSeriesSlugAndChapter(url, out var slug, out var chapter);

        Assert.True(parsed);
        Assert.Equal(expectedSlug, slug);
        Assert.Equal(expectedChapter, chapter);
    }

    [Fact]
    public void TryParseSeriesSlugAndChapter_SeriesPageOnly_ReturnsSlugWithoutChapter()
    {
        var parsed = SeriesExtractionFallback.TryParseSeriesSlugAndChapter(
            "https://comizy.io/nano-machine", out var slug, out var chapter);

        Assert.True(parsed);
        Assert.Equal("nano-machine", slug);
        Assert.Null(chapter);
    }

    [Fact]
    public void TryParseSeriesSlugAndChapter_InvalidUrl_ReturnsFalse()
    {
        var parsed = SeriesExtractionFallback.TryParseSeriesSlugAndChapter("not a url", out var slug, out var chapter);

        Assert.False(parsed);
        Assert.Null(slug);
        Assert.Null(chapter);
    }
}
