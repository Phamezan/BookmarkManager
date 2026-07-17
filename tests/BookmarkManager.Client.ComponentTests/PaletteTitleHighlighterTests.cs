using BookmarkManager.Client.Components.CommandPalette;

namespace BookmarkManager.Client.ComponentTests;

public sealed class PaletteTitleHighlighterTests
{
    [Fact]
    public void BuildTitleHtml_CaseInsensitiveMatch_WrapsMark()
    {
        var html = PaletteTitleHighlighter.BuildTitleHtml("One Piece Chapter", "piece").Value;
        Assert.Equal("One <mark class=\"palette-highlight\">Piece</mark> Chapter", html);
    }

    [Fact]
    public void BuildTitleHtml_MultiToken_HighlightsEach()
    {
        var html = PaletteTitleHighlighter.BuildTitleHtml("Solo Leveling Manhwa", "solo manhwa").Value;
        Assert.Contains("<mark class=\"palette-highlight\">Solo</mark>", html);
        Assert.Contains("<mark class=\"palette-highlight\">Manhwa</mark>", html);
    }

    [Fact]
    public void BuildTitleHtml_OverlappingTokens_MergesRanges()
    {
        var html = PaletteTitleHighlighter.BuildTitleHtml("abcdef", "abc cdef").Value;
        Assert.Equal("<mark class=\"palette-highlight\">abcdef</mark>", html);
    }

    [Fact]
    public void BuildTitleHtml_TitleWithScript_EncodesAndDoesNotInject()
    {
        var html = PaletteTitleHighlighter.BuildTitleHtml("<script>alert(1)</script>", "script").Value;
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;", html);
        Assert.Contains("&gt;", html);
        Assert.Contains("<mark class=\"palette-highlight\">script</mark>", html);
        // Raw angle-bracket tags must never appear unencoded outside our mark wrapper.
        Assert.DoesNotContain("<script>", html.Replace("<mark class=\"palette-highlight\">script</mark>", ""));
    }

    [Fact]
    public void BuildTitleHtml_NoTokenMatch_ReturnsEncodedPlainTitle()
    {
        var html = PaletteTitleHighlighter.BuildTitleHtml("One Piece <x>", "naruto").Value;
        Assert.Equal("One Piece &lt;x&gt;", html);
        Assert.DoesNotContain("palette-highlight", html);
    }

    [Fact]
    public void BuildTitleHtml_EmptyQuery_ReturnsEncodedPlainTitle()
    {
        var html = PaletteTitleHighlighter.BuildTitleHtml("Hello & World", "").Value;
        Assert.Equal("Hello &amp; World", html);
    }

    [Fact]
    public void BuildTitleHtml_AccentInsensitive_MatchesPokemon()
    {
        var html = PaletteTitleHighlighter.BuildTitleHtml("Pokémon Adventures", "pokemon").Value;
        Assert.Contains("palette-highlight", html);
        Assert.DoesNotContain("Adventures</mark>", html); // only the pokemon part marked
        Assert.StartsWith("<mark class=\"palette-highlight\">", html);
    }
}
