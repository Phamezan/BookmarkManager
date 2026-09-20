using BookmarkManager.Api.Services.Backup;

namespace BookmarkManager.UnitTests.Backup;

public sealed class NetscapeBookmarkHtmlParserTests
{
    [Fact]
    public void Parse_MapsChromiumRootsWithoutCreatingWrapperFolders()
    {
        const string html = """
            <!DOCTYPE NETSCAPE-Bookmark-file-1>
            <DL><p>
                <DT><H3 PERSONAL_TOOLBAR_FOLDER="true">Bookmarks bar</H3>
                <DL><p>
                    <DT><H3>Anime</H3>
                    <DL><p>
                        <DT><A HREF="https://example.com/watch">Watch</A>
                    </DL><p>
                </DL><p>
                <DT><H3 UNFILED_BOOKMARKS_FOLDER="true">Other bookmarks</H3>
                <DL><p>
                    <DT><A HREF="https://example.com/other">Other</A>
                </DL><p>
            </DL><p>
            """;

        var roots = NetscapeBookmarkHtmlParser.Parse(html);

        var bar = Assert.Single(roots, root => root.BrowserRootId == "1");
        var anime = Assert.Single(bar.Children);
        Assert.Equal("Anime", anime.Title);
        Assert.True(anime.IsFolder);
        Assert.Equal("https://example.com/watch", Assert.Single(anime.Children).Url);

        var other = Assert.Single(roots, root => root.BrowserRootId == "2");
        Assert.Equal("Other", Assert.Single(other.Children).Title);
        Assert.DoesNotContain(roots.SelectMany(root => root.Children), node => node.Title == "Bookmarks bar");
        Assert.DoesNotContain(roots.SelectMany(root => root.Children), node => node.Title == "Other bookmarks");
    }

    [Fact]
    public void Parse_RecognizesBookmarkManagerAndMobileRoots()
    {
        const string html = """
            <!DOCTYPE NETSCAPE-Bookmark-file-1>
            <DL><p>
                <DT><H3>Bookmarks bar</H3>
                <DL><p><DT><A HREF="https://example.com/">Example</A></DL><p>
                <DT><H3 MOBILE_BOOKMARKS_FOLDER="true">Mobile bookmarks</H3>
                <DL><p><DT><A HREF="https://m.example.com/">Mobile</A></DL><p>
            </DL><p>
            """;

        var roots = NetscapeBookmarkHtmlParser.Parse(html);

        Assert.Equal("Example", Assert.Single(roots.Single(root => root.BrowserRootId == "1").Children).Title);
        Assert.Equal("Mobile", Assert.Single(roots.Single(root => root.BrowserRootId == "3").Children).Title);
    }

    [Fact]
    public void Parse_SkipsNonHttpBookmarks()
    {
        const string html = """
            <!DOCTYPE NETSCAPE-Bookmark-file-1>
            <DL><p>
                <DT><H3 PERSONAL_TOOLBAR_FOLDER="true">Bookmarks bar</H3>
                <DL><p>
                    <DT><A HREF="javascript:alert(1)">Script</A>
                    <DT><A HREF="file:///tmp/test">Local file</A>
                    <DT><A HREF="https://example.com/safe">Safe</A>
                </DL><p>
            </DL><p>
            """;

        var root = Assert.Single(NetscapeBookmarkHtmlParser.Parse(html));
        var bookmark = Assert.Single(root.Children);
        Assert.Equal("Safe", bookmark.Title);
        Assert.Equal("https://example.com/safe", bookmark.Url);
    }

    [Fact]
    public void Parse_RejectsNonBookmarkHtml()
    {
        var ex = Assert.Throws<InvalidDataException>(
            () => NetscapeBookmarkHtmlParser.Parse("<html><body>hello</body></html>"));

        Assert.Contains("not a Netscape/Chromium bookmark HTML export", ex.Message);
    }
}
