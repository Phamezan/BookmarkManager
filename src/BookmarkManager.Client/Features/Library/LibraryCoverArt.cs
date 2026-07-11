namespace BookmarkManager.Client.Features.Library;

/// <summary>
/// Cover art for a library entry: a real provider cover image when one is available, otherwise a
/// deterministic duotone gradient derived from the title. Fixed hues on purpose — covers are
/// artwork and do not re-theme with the app palette.
/// </summary>
public static class LibraryCoverArt
{
    private static readonly (string A, string B)[] Gradients =
    {
        ("#141E30", "#3A6073"),
        ("#42275A", "#734B6D"),
        ("#1A2980", "#26D0CE"),
        ("#2C3E50", "#FD746C"),
        ("#0F2027", "#2C5364"),
        ("#3A1C71", "#D76D77"),
        ("#4B134F", "#C94B4B"),
        ("#134E5E", "#71B280"),
        ("#1F1C2C", "#928DAB"),
        ("#360033", "#0B8793"),
    };

    public static bool HasCoverImage(LibraryItem item) => !string.IsNullOrWhiteSpace(item.CoverImageUrl);

    public static string StyleFor(LibraryItem item)
    {
        var hash = item.Title.Aggregate(17, (h, c) => unchecked(h * 31 + c));
        var (a, b) = Gradients[Math.Abs(hash) % Gradients.Length];
        var style = $"--cover-a: {a}; --cover-b: {b};";

        return HasCoverImage(item)
            ? $"{style} --cover-image: url('{EscapeCssUrl(item.CoverImageUrl!)}');"
            : style;
    }

    public static string CoverClass(LibraryItem item) => HasCoverImage(item) ? "has-cover-image" : "no-cover-image";

    private static string EscapeCssUrl(string url) => url.Replace("'", "%27", StringComparison.Ordinal).Replace("\"", "%22", StringComparison.Ordinal);
}
