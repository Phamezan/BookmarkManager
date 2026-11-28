using BookmarkManager.Api.Services.Novels;
using Xunit;

namespace BookmarkManager.UnitTests.Novels;

public class NovelHtmlSanitizerTests
{
    private readonly NovelHtmlSanitizer _sanitizer = new();

    [Fact]
    public void Sanitize_AllowsStructuralAndInlineFormattingTags()
    {
        var input = "<h1>Title</h1><h2>Sub</h2><h3>H3</h3><h4>H4</h4>" +
                    "<p>Hello <em>em</em> <strong>strong</strong> <i>i</i> <b>b</b> <u>u</u> <s>s</s><br>Line 2</p>" +
                    "<hr><blockquote>Quote</blockquote><ul><li>One</li></ul><ol><li>Two</li></ol>";

        var result = _sanitizer.Sanitize(input);

        Assert.Contains("<h1>Title</h1>", result);
        Assert.Contains("<h2>Sub</h2>", result);
        Assert.Contains("<h3>H3</h3>", result);
        Assert.Contains("<h4>H4</h4>", result);
        Assert.Contains("<em>em</em>", result);
        Assert.Contains("<strong>strong</strong>", result);
        Assert.Contains("<i>i</i>", result);
        Assert.Contains("<b>b</b>", result);
        Assert.Contains("<u>u</u>", result);
        Assert.Contains("<s>s</s>", result);
        Assert.Contains("<br>", result);
        Assert.Contains("<hr>", result);
        Assert.Contains("<blockquote>Quote</blockquote>", result);
        Assert.Contains("<ul><li>One</li></ul>", result);
        Assert.Contains("<ol><li>Two</li></ol>", result);
    }

    [Fact]
    public void Sanitize_StripsScriptIframeStyleAndEventHandlers()
    {
        var input = "<p onclick=\"alert(1)\" style=\"color:red\" class=\"evil\">Safe</p>" +
                    "<script>window.pwned = true;</script>" +
                    "<style>body { display:none; }</style>" +
                    "<iframe src=\"https://evil.example/frame\">fallback</iframe>" +
                    "<img src=\"https://cdn.example.com/ok.png\" onerror=\"alert(1)\" class=\"pic\" style=\"width:100%\" alt=\"x\">";

        var result = _sanitizer.Sanitize(input);

        Assert.Contains("<p>Safe</p>", result);
        Assert.DoesNotContain("script", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pwned", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("iframe", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fallback", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("class", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onclick", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alt=", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<img src=\"https://cdn.example.com/ok.png\">", result);
    }

    [Theory]
    [InlineData("<img src=\"javascript:alert(1)\">")]
    [InlineData("<img src=\"data:image/svg+xml,<svg onload=alert(1)>\">")]
    [InlineData("<img src=\"/relative/path.png\">")]
    [InlineData("<img src=\"images/local.png\">")]
    [InlineData("<img src=\"//protocol-relative.example/a.png\">")]
    [InlineData("<img>")]
    public void Sanitize_DropsImgWhenSrcIsNotAbsoluteHttpOrHttps(string input)
    {
        var result = _sanitizer.Sanitize($"<p>Before</p>{input}<p>After</p>");

        Assert.Contains("<p>Before</p>", result);
        Assert.Contains("<p>After</p>", result);
        Assert.DoesNotContain("<img", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_StripsLinksAndWrapperDivsWhilePreservingInnerText()
    {
        var input = "<div class=\"wrapper\"><p>Visit <a href=\"https://evil.example\">here</a> now.</p></div>";

        var result = _sanitizer.Sanitize(input);

        Assert.DoesNotContain("<div", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<a", result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<p>Visit here now.</p>", result);
    }
}
