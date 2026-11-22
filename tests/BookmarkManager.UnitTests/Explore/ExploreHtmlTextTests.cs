using BookmarkManager.Api.Services.Explore;

namespace BookmarkManager.UnitTests.Explore;

public sealed class ExploreHtmlTextTests
{
    [Fact]
    public void BreaksBecomeNewlinesAndOtherTagsAreDropped()
        => Assert.Equal("Line one\nLine two bold", ExploreHtmlText.Strip("Line one<br>Line two <b>bold</b>"));

    [Fact]
    public void SelfClosingBreakAndUppercaseAreHandled()
        => Assert.Equal("a\nb", ExploreHtmlText.Strip("a<BR />b"));

    [Fact]
    public void EntitiesAreDecoded()
        => Assert.Equal("Tom & Jerry's \"tale\"", ExploreHtmlText.Strip("Tom &amp; Jerry&#39;s &quot;tale&quot;"));

    [Fact]
    public void ScriptElementsAreRemovedWithTheirContent()
    {
        var text = ExploreHtmlText.Strip("Hi<script>alert(1)</script> there<style>p{}</style>");

        Assert.Equal("Hi there", text);
    }

    [Fact]
    public void EncodedMarkupSurvivesOnlyAsInertText()
    {
        var text = ExploreHtmlText.Strip("&lt;script&gt;x&lt;/script&gt;");

        Assert.Equal("<script>x</script>", text);
    }

    [Fact]
    public void BlankRunsCollapseAndEmptyInputIsNull()
    {
        Assert.Equal("a\n\nb", ExploreHtmlText.Strip("a<br><br><br><br>b"));
        Assert.Null(ExploreHtmlText.Strip("  <br> "));
        Assert.Null(ExploreHtmlText.Strip(null));
    }
}
