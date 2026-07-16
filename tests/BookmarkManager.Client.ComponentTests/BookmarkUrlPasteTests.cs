using BookmarkManager.Client.Features.Bookmarks;

namespace BookmarkManager.Client.ComponentTests;

public sealed class BookmarkUrlPasteTests
{
    [Theory]
    [InlineData("https://example.com/chapter-124", "https://example.com/chapter-124")]
    [InlineData("http://example.com", "http://example.com/")]
    public void TryParseHttpUrl_ValidHttpOrHttps_ReturnsTrue(string input, string expectedUrl)
    {
        var result = BookmarkUrlPaste.TryParseHttpUrl(input, out var url, out var error);

        Assert.True(result);
        Assert.Equal(expectedUrl, url);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void TryParseHttpUrl_TrimsSurroundingWhitespace()
    {
        var result = BookmarkUrlPaste.TryParseHttpUrl("  https://example.com/  ", out var url, out _);

        Assert.True(result);
        Assert.Equal("https://example.com/", url);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    public void TryParseHttpUrl_RejectsNonHttpScheme(string input)
    {
        var result = BookmarkUrlPaste.TryParseHttpUrl(input, out var url, out var error);

        Assert.False(result);
        Assert.Equal(string.Empty, url);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData("/relative/path")]
    [InlineData("example.com/no-scheme")]
    [InlineData("not a url at all")]
    public void TryParseHttpUrl_RejectsRelativeOrInvalidUrl(string input)
    {
        var result = BookmarkUrlPaste.TryParseHttpUrl(input, out var url, out var error);

        Assert.False(result);
        Assert.Equal(string.Empty, url);
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryParseHttpUrl_RejectsEmptyOrWhitespaceClipboard(string? input)
    {
        var result = BookmarkUrlPaste.TryParseHttpUrl(input, out var url, out var error);

        Assert.False(result);
        Assert.Equal(string.Empty, url);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryParseHttpUrl_RejectsOverlengthClipboard()
    {
        var overlong = "https://example.com/" + new string('a', BookmarkUrlPaste.MaxClipboardLength);

        var result = BookmarkUrlPaste.TryParseHttpUrl(overlong, out var url, out var error);

        Assert.False(result);
        Assert.Equal(string.Empty, url);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryParseHttpUrl_AtMaxLength_StillAccepted()
    {
        var host = "https://example.com/";
        var padding = new string('a', BookmarkUrlPaste.MaxClipboardLength - host.Length);
        var atLimit = host + padding;

        var result = BookmarkUrlPaste.TryParseHttpUrl(atLimit, out var url, out _);

        Assert.True(result);
        Assert.Equal(atLimit, url);
    }
}
