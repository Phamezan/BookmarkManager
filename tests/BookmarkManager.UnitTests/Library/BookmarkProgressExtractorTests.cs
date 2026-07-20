using BookmarkManager.Api.Services.Library;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

public sealed class BookmarkProgressExtractorTests
{
    [Fact]
    public void Extract_ChapterInTitleTail_ReturnsChapterAndRawText()
    {
        var result = BookmarkProgressExtractor.Extract("Solo Leveling - Chapter 127", null);

        Assert.Equal(127, result.CurrentChapter);
        Assert.Equal("Chapter 127", result.RawProgressText);
        Assert.Contains("Solo Leveling", result.SeriesName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Extract_VolumeAndChapterInTitle_ReturnsCombinedRawTextAndChapterNumber()
    {
        var result = BookmarkProgressExtractor.Extract("Mushoku Tensei - Vol 3 Ch 23", null);

        Assert.Equal(23, result.CurrentChapter);
        Assert.Equal("Vol 3 Ch 23", result.RawProgressText);
    }

    [Fact]
    public void Extract_ChapterInUrlSlug_UsedWhenTitleHasNoMarker()
    {
        var result = BookmarkProgressExtractor.Extract(
            "One Piece | MangaDex", "https://mangadex.org/one-piece/chapter-1090");

        Assert.Equal(1090, result.CurrentChapter);
        Assert.Equal("Chapter 1090", result.RawProgressText);
    }

    [Theory]
    [InlineData("86")]
    [InlineData("1/11")]
    public void Extract_NumericOnlyCanonicalTitle_DoesNotExtractChapter(string title)
    {
        var result = BookmarkProgressExtractor.Extract(title, null);

        Assert.Null(result.CurrentChapter);
        Assert.Null(result.RawProgressText);
    }

    [Fact]
    public void Extract_DecimalChapter_ParsesAsDouble()
    {
        var result = BookmarkProgressExtractor.Extract("Omniscient Reader - Chapter 112.5", null);

        Assert.Equal(112.5, result.CurrentChapter);
    }

    [Fact]
    public void Extract_NoProgressMarker_ReturnsNullChapterAndRawText()
    {
        var result = BookmarkProgressExtractor.Extract("Attack on Titan", "https://example.com/attack-on-titan");

        Assert.Null(result.CurrentChapter);
        Assert.Null(result.RawProgressText);
    }

    [Fact]
    public void Extract_WebtoonArcTitle_UsesHighestProgressNumber()
    {
        var result = BookmarkProgressExtractor.Extract("[Chapter 2] Ep. 31 | GOSU", null);

        Assert.Equal(31, result.CurrentChapter);
        Assert.Equal("Ep. 31", result.RawProgressText);
    }

    [Fact]
    public void Extract_MixedEpisodeAndChapterTitle_UsesHighestProgressNumber()
    {
        var result = BookmarkProgressExtractor.Extract("Some Series - Episode 3 Chapter 200", null);

        Assert.Equal(200, result.CurrentChapter);
        Assert.Equal("Chapter 200", result.RawProgressText);
    }

    [Fact]
    public void Extract_WebtoonArcSlug_UsesHighestProgressNumber()
    {
        var result = BookmarkProgressExtractor.Extract(
            "GOSU",
            "https://www.webtoons.com/en/action/gosu/chapter-2-ep-31/viewer?title_no=1099&episode_no=118");

        Assert.Equal(31, result.CurrentChapter);
        Assert.Equal("Episode 31", result.RawProgressText);
    }

    [Fact]
    public void Extract_WebtoonArcSlug_PrefersEpisodeWhenChapterNumberIsHigher()
    {
        var result = BookmarkProgressExtractor.Extract(
            "Some Series",
            "https://www.webtoons.com/en/action/foo/chapter-12-ep-5/viewer");

        Assert.Equal(5, result.CurrentChapter);
        Assert.Equal("Episode 5", result.RawProgressText);
    }

    [Fact]
    public void Extract_WebtoonBookmarkTitleAndUrl_UsesEpisodeFromTitle()
    {
        var result = BookmarkProgressExtractor.Extract(
            "[Chapter 2] Ep. 31 | GOSU",
            "https://www.webtoons.com/en/action/gosu/chapter-2-ep-31/viewer?title_no=1099&episode_no=118");

        Assert.Equal(31, result.CurrentChapter);
        Assert.Equal("Ep. 31", result.RawProgressText);
    }

    [Fact]
    public void Extract_MiruroUrlWithWatchInPath_DoesNotFalseMatchChapterFromMediaId()
    {
        var result = BookmarkProgressExtractor.Extract(
            "Watch Wistoria: Wand and Sword Season 2 · Miruro",
            "https://www.miruro.to/watch/182300/wistoria-wand-and-sword-season-2?ep=11");

        Assert.Equal(11, result.CurrentChapter);
        Assert.Equal("Episode 11", result.RawProgressText);
    }

    [Fact]
    public void Extract_MiruroUrlSecondRepro_DoesNotFalseMatchChapterFromMediaId()
    {
        var result = BookmarkProgressExtractor.Extract(
            "Watch Frieren: Beyond Journey's End Season 2 · Miruro",
            "https://www.miruro.tv/watch/182255/sousou-no-frieren-2nd-season?ep=10");

        Assert.Equal(10, result.CurrentChapter);
        Assert.Equal("Episode 10", result.RawProgressText);
    }

    [Fact]
    public void Extract_WatchInPathWithoutEpisodeQuery_DoesNotExtractAnything()
    {
        var result = BookmarkProgressExtractor.Extract("Some Show", "https://example.com/watch/99999");

        Assert.Null(result.CurrentChapter);
        Assert.Null(result.RawProgressText);
    }

    [Fact]
    public void Extract_OpaqueEpisodeIdHost_DoesNotTrustEpisodeQueryParam()
    {
        var result = BookmarkProgressExtractor.Extract(
            "Watch That Time I Got Reincarnated as a Slime Season 3",
            "https://aniwatchtv.to/watch/that-time-i-got-reincarnated-as-a-slime-season-3-19109?ep=128062");

        Assert.Null(result.CurrentChapter);
        Assert.Null(result.RawProgressText);
    }
}
