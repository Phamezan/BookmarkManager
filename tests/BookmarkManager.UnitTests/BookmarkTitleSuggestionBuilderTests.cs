using BookmarkManager.Api.Services.BookmarkTagging;
using Xunit;

namespace BookmarkManager.UnitTests;

public sealed class BookmarkTitleSuggestionBuilderTests
{
    [Fact]
    public void Build_ChapterFromTitle_UsesChapterLabelAndEmDash()
    {
        var suggested = BookmarkTitleSuggestionBuilder.Build(
            "Solo Leveling",
            "Solo Leveling Ch. 12",
            "https://example.com/solo");

        Assert.Equal("Solo Leveling — Chapter 12", suggested);
    }

    [Fact]
    public void Build_EpisodeFromTitle_UsesEpisodeLabel()
    {
        var suggested = BookmarkTitleSuggestionBuilder.Build(
            "One Piece",
            "One Piece Episode 1092",
            "https://example.com/op");

        Assert.Equal("One Piece — Episode 1092", suggested);
    }

    [Fact]
    public void Build_ChapterFromUrl_AppendsWhenTitleHasNoProgress()
    {
        var suggested = BookmarkTitleSuggestionBuilder.Build(
            "Solo Leveling",
            "Solo Leveling",
            "https://example.com/title/solo-leveling/chapter/42");

        Assert.Equal("Solo Leveling — Chapter 42", suggested);
    }

    [Fact]
    public void Build_CanonicalOnly_WhenNoProgressInTitleOrUrl()
    {
        var suggested = BookmarkTitleSuggestionBuilder.Build(
            "The Gamer's POV",
            "The Gamer's POV",
            "https://example.com/book/the-gamers-pov");

        Assert.Equal("The Gamer's POV", suggested);
    }

    [Fact]
    public void Build_NullOrBlankCanonical_ReturnsNull()
    {
        Assert.Null(BookmarkTitleSuggestionBuilder.Build(null, "Title Ch. 1", "https://example.com/x"));
        Assert.Null(BookmarkTitleSuggestionBuilder.Build("   ", "Title Ch. 1", "https://example.com/x"));
    }

    [Theory]
    [InlineData("Solo Leveling — Chapter 12", "solo leveling ch. 12", true)]
    [InlineData("Solo Leveling — Chapter 12", "Solo Leveling — Chapter 12", false)]
    [InlineData(null, "Anything", false)]
    [InlineData("  ", "Anything", false)]
    public void DiffersFromCurrent_ComparesTrimmedOrdinalIgnoreCase(
        string? suggested,
        string? current,
        bool expected)
    {
        Assert.Equal(expected, BookmarkTitleSuggestionBuilder.DiffersFromCurrent(suggested, current));
    }

    [Fact]
    public void Build_EpisodeFromUrl_WhenTitleHasNoMarker()
    {
        var suggested = BookmarkTitleSuggestionBuilder.Build(
            "One Piece",
            "One Piece",
            "https://example.com/watch/one-piece/ep-900");

        Assert.Equal("One Piece — Episode 900", suggested);
    }

    [Fact]
    public void Build_WholeNumber_OmitsDecimal()
    {
        var suggested = BookmarkTitleSuggestionBuilder.Build(
            "Series",
            "Series Ch. 3.0",
            null);

        // Extractor parses 3.0 as 3.0; FormatProgress should print "3" for whole numbers.
        Assert.Equal("Series — Chapter 3", suggested);
    }

    [Fact]
    public void Build_TrimsTrailingHashFromCanonical()
    {
        var suggested = BookmarkTitleSuggestionBuilder.Build(
            "I Can Make Everything Level UP #",
            "I Can Make Everything Level UP #Chapter 46 - Cooperation (2)",
            "https://example.com/novel/chapter-46");

        Assert.Equal("I Can Make Everything Level UP — Chapter 46", suggested);
    }
}
