using BookmarkManager.Client.Features.Bookmarks;
using BookmarkManager.Contracts;

namespace BookmarkManager.Client.ComponentTests;

public sealed class BookmarkHostFilterTests
{
    [Theory]
    [InlineData("https://www.example.com/foo", "example.com")]
    [InlineData("https://example.com/foo", "example.com")]
    [InlineData("https://EXAMPLE.com/foo", "example.com")]
    [InlineData("http://sub.example.com/foo", "sub.example.com")]
    public void NormalizeHost_LowercasesAndStripsWww(string url, string expected)
    {
        Assert.Equal(expected, BookmarkHostFilter.NormalizeHost(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("/relative/path")]
    public void NormalizeHost_InvalidOrEmpty_ReturnsNull(string? url)
    {
        Assert.Null(BookmarkHostFilter.NormalizeHost(url));
    }

    private static BookmarkNodeDto MakeBookmark(string? url) =>
        new() { Id = Guid.NewGuid(), Title = "Item", Type = NodeType.Bookmark, Url = url };

    [Fact]
    public void CountHosts_OnlyKeepsHostsAppearingTwoOrMoreTimes()
    {
        var items = new List<BookmarkNodeDto>
        {
            MakeBookmark("https://www.example.com/1"),
            MakeBookmark("https://example.com/2"),
            MakeBookmark("https://onceonly.com/1"),
        };

        var result = BookmarkHostFilter.CountHosts(items);

        Assert.Single(result);
        Assert.Equal("example.com", result[0].Tag);
        Assert.Equal(2, result[0].Count);
    }

    [Fact]
    public void CountHosts_OrdersByCountDescendingThenHostNameAscending()
    {
        var items = new List<BookmarkNodeDto>
        {
            MakeBookmark("https://a-host.com/1"),
            MakeBookmark("https://a-host.com/2"),
            MakeBookmark("https://b-host.com/1"),
            MakeBookmark("https://b-host.com/2"),
            MakeBookmark("https://b-host.com/3"),
        };

        var result = BookmarkHostFilter.CountHosts(items);

        Assert.Equal(["b-host.com", "a-host.com"], result.Select(r => r.Tag));
    }

    [Fact]
    public void CountHosts_TiedCounts_OrdersHostNameAscending()
    {
        var items = new List<BookmarkNodeDto>
        {
            MakeBookmark("https://zeta.com/1"),
            MakeBookmark("https://zeta.com/2"),
            MakeBookmark("https://alpha.com/1"),
            MakeBookmark("https://alpha.com/2"),
        };

        var result = BookmarkHostFilter.CountHosts(items);

        Assert.Equal(["alpha.com", "zeta.com"], result.Select(r => r.Tag));
    }

    [Fact]
    public void CountHosts_SkipsInvalidAndEmptyUrls()
    {
        var items = new List<BookmarkNodeDto>
        {
            MakeBookmark(null),
            MakeBookmark(""),
            MakeBookmark("https://dup.com/1"),
            MakeBookmark("https://dup.com/2"),
        };

        var result = BookmarkHostFilter.CountHosts(items);

        Assert.Single(result);
        Assert.Equal("dup.com", result[0].Tag);
    }
}
