using BookmarkManager.Api.Services.Suwayomi;
using Xunit;

namespace BookmarkManager.UnitTests.Suwayomi;

public sealed class SuwayomiMatchScorerTests
{
    [Fact]
    public void Classify_NearMiss_IsNotHigh()
    {
        var score = SuwayomiMatchScorer.Score("The Origin", "The Original");
        var exact = SuwayomiMatchScorer.IsExact("The Origin", "The Original");

        Assert.False(exact);
        Assert.NotEqual("High", SuwayomiMatchScorer.Classify(score, exact));
    }

    [Fact]
    public void Classify_HighSimilarityRemake_IsNotHigh()
    {
        const string title = "The Great Mage Returns After 4000 Years";
        var score = SuwayomiMatchScorer.Score(title, title + " Remake");
        var exact = SuwayomiMatchScorer.IsExact(title, title + " Remake");

        Assert.False(exact);
        Assert.True(score >= 0.85, $"precondition: near-identical remake title scores high ({score})");
        Assert.Equal("Medium", SuwayomiMatchScorer.Classify(score, exact));
    }

    [Fact]
    public void Classify_ExactNormalized_IsHighEvenWithPunctuationAndCase()
    {
        var exact = SuwayomiMatchScorer.IsExact("solo-leveling", "Solo Leveling");

        Assert.True(exact);
        Assert.Equal("High", SuwayomiMatchScorer.Classify(0.5, exact));
    }

    [Fact]
    public void Score_IdenticalTitles_IsOne()
    {
        Assert.Equal(1.0, SuwayomiMatchScorer.Score("Nano Machine", "Nano Machine"), 3);
    }

    [Theory]
    [InlineData("330", 330)]
    [InlineData("330.9", 330)]
    [InlineData("67", 67)]
    public void ProgressFromChapter_ReturnsIntegerPart(string chapter, int expected)
    {
        Assert.Equal(expected, SuwayomiMatchScorer.ProgressFromChapter(chapter));
    }

    [Fact]
    public void ProgressFromChapter_Unknown_IsNull()
    {
        Assert.Null(SuwayomiMatchScorer.ProgressFromChapter(null));
        Assert.Null(SuwayomiMatchScorer.ProgressFromChapter("unknown"));
    }

    [Fact]
    public void SelectChaptersToMark_IncludesChapterZeroAndDecimals()
    {
        var chapters = new[]
        {
            new SuwayomiChapter(1, 0, false),
            new SuwayomiChapter(2, 1, false),
            new SuwayomiChapter(3, 5, false),
            new SuwayomiChapter(4, 5.5, false),
            new SuwayomiChapter(5, 10, false)
        };

        var ids = SuwayomiMatchScorer.SelectChaptersToMark(chapters, "5.5");

        Assert.Equal([1, 2, 3, 4], ids);
    }

    [Fact]
    public void SelectChaptersToMark_ChapterBeyondSourceMax_MarksNothing()
    {
        var chapters = new[]
        {
            new SuwayomiChapter(1, 1, false),
            new SuwayomiChapter(2, 2, false)
        };

        Assert.Empty(SuwayomiMatchScorer.SelectChaptersToMark(chapters, "12"));
    }

    [Fact]
    public void SelectChaptersToMark_UnknownChapter_MarksNothing()
    {
        var chapters = new[] { new SuwayomiChapter(1, 1, false) };

        Assert.Empty(SuwayomiMatchScorer.SelectChaptersToMark(chapters, null));
    }
}
