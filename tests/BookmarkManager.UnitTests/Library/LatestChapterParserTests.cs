using BookmarkManager.Contracts;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

public sealed class LatestChapterParserTests
{
    [Theory]
    [InlineData("254", 254)]
    [InlineData("3092", 3092)]
    [InlineData("Chapter 228", 228)]
    [InlineData("chapter 3090", 3090)]
    [InlineData("Ch. 42", 42)]
    [InlineData("Chapter 3090 Born Into an Endless War", 3090)]
    public void Parse_ExtractsPlainOrPrefixedChapterNumbers(string input, double expected) =>
        Assert.Equal(expected, LibraryLatestChapterParser.Parse(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Vol 12 Ch 78")]
    [InlineData("Ongoing")]
    public void Parse_ReturnsNullForAmbiguousOrMissingValues(string? input) =>
        Assert.Null(LibraryLatestChapterParser.Parse(input));
}
