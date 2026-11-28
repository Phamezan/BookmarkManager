using AngleSharp.Dom;
using Ganss.Xss;

namespace BookmarkManager.Api.Services.Novels;

/// <summary>
/// Server-side sanitizer for untrusted novel chapter HTML rendered inside the Suwayomi origin.
/// Allows only a strict structural/text tag allowlist and strips every attribute except
/// <c>img[src]</c> with an absolute <c>http</c> or <c>https</c> URL (otherwise drops the <c>img</c>).
/// </summary>
public sealed class NovelHtmlSanitizer
{
    private static readonly string[] AllowedTags =
    [
        "p", "br", "em", "strong", "i", "b", "u", "s", "hr", "blockquote",
        "h1", "h2", "h3", "h4", "ul", "ol", "li", "img"
    ];

    private static readonly HashSet<string> DropSubtreeTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "iframe", "object", "embed", "applet", "svg", "math",
        "template", "noscript", "head", "title", "textarea", "select", "option", "button"
    };

    private readonly ThreadLocal<HtmlSanitizer> _sanitizer = new(CreateSanitizer);

    public string Sanitize(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        return _sanitizer.Value!.Sanitize(html);
    }

    private static HtmlSanitizer CreateSanitizer()
    {
        var options = new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(AllowedTags, StringComparer.OrdinalIgnoreCase),
            AllowedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" },
            AllowedSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "http", "https" },
            AllowedCssProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            AllowedAtRules = new HashSet<AngleSharp.Css.Dom.CssRuleType>(),
            UriAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "src" }
        };

        var sanitizer = new HtmlSanitizer(options)
        {
            KeepChildNodes = true,
            AllowDataAttributes = false
        };
        sanitizer.AllowedClasses.Clear();

        sanitizer.RemovingTag += (_, args) =>
        {
            if (DropSubtreeTags.Contains(args.Tag.TagName))
            {
                args.Tag.InnerHtml = string.Empty;
            }
        };

        sanitizer.PostProcessNode += (_, args) =>
        {
            if (args.Node is not IElement element)
            {
                return;
            }

            if (element.TagName.Equals("IMG", StringComparison.OrdinalIgnoreCase))
            {
                var src = element.GetAttribute("src")?.Trim();
                if (string.IsNullOrEmpty(src)
                    || !Uri.TryCreate(src, UriKind.Absolute, out var uri)
                    || uri.Scheme is not ("http" or "https"))
                {
                    args.ReplacementNodes.Clear();
                    element.Remove();
                    return;
                }

                // Strip anything other than src, just in case.
                foreach (var attr in element.Attributes.ToList())
                {
                    if (!attr.Name.Equals("src", StringComparison.OrdinalIgnoreCase))
                    {
                        element.RemoveAttribute(attr.Name);
                    }
                }
            }
            else if (element.Attributes.Length > 0)
            {
                foreach (var attr in element.Attributes.ToList())
                {
                    element.RemoveAttribute(attr.Name);
                }
            }
        };

        return sanitizer;
    }
}
