using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services;
using BookmarkManager.Contracts;
using Xunit;

namespace BookmarkManager.UnitTests;

public sealed class BookmarkPlanToReadHeuristicTests
{
    [Theory]
    [InlineData("https://novelfire.net/book/solo-leveling")]
    [InlineData("https://www.royalroad.com/fiction/21220/mother-of-learning")]
    [InlineData("https://mangadex.org/title/abc-def/solo-leveling")]
    public void ShouldMarkPlanToRead_SeriesRootUrl_ReturnsTrue(string url)
    {
        Assert.True(BookmarkPlanToReadHeuristic.ShouldMarkPlanToRead(url));
    }

    [Theory]
    [InlineData("https://novelfire.net/book/solo-leveling/chapter-12")]
    [InlineData("https://example.com/series/foo/ch-5")]
    [InlineData("https://example.com/manga/bar/123")]
    [InlineData("https://example.com/read?chapter=9")]
    [InlineData("https://example.com/")]
    [InlineData("https://example.com/only-one-segment")]
    [InlineData("ftp://example.com/book/title")]
    [InlineData("")]
    [InlineData(null)]
    public void ShouldMarkPlanToRead_ChapterOrInvalid_ReturnsFalse(string? url)
    {
        Assert.False(BookmarkPlanToReadHeuristic.ShouldMarkPlanToRead(url));
    }

    [Fact]
    public void ApplyAutoStatus_ChapterLessUrl_SetsPlanToRead()
    {
        var node = new BookmarkNode
        {
            Type = NodeType.Bookmark,
            Url = "https://novelfire.net/book/solo-leveling",
            Status = null
        };

        BookmarkPlanToReadHeuristic.ApplyAutoStatus(node);

        Assert.Equal(BookmarkReadingStatus.PlanToRead, node.Status);
    }

    [Fact]
    public void ApplyAutoStatus_ChapterUrlClearsExistingPlanToRead()
    {
        var node = new BookmarkNode
        {
            Type = NodeType.Bookmark,
            Url = "https://novelfire.net/book/solo-leveling/chapter-12",
            Status = BookmarkReadingStatus.PlanToRead
        };

        BookmarkPlanToReadHeuristic.ApplyAutoStatus(node);

        Assert.Null(node.Status);
    }

    [Fact]
    public void ApplyAutoStatus_PreservesExplicitReadingStatus()
    {
        var node = new BookmarkNode
        {
            Type = NodeType.Bookmark,
            Url = "https://novelfire.net/book/solo-leveling",
            Status = BookmarkReadingStatus.Reading
        };

        BookmarkPlanToReadHeuristic.ApplyAutoStatus(node);

        Assert.Equal(BookmarkReadingStatus.Reading, node.Status);
    }

    [Fact]
    public void ApplyAutoStatus_IgnoresFolders()
    {
        var node = new BookmarkNode
        {
            Type = NodeType.Folder,
            Url = null,
            Status = null,
            Title = "Manga"
        };

        BookmarkPlanToReadHeuristic.ApplyAutoStatus(node);

        Assert.Null(node.Status);
    }
}
