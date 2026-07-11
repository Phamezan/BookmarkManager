namespace BookmarkManager.Contracts;

public sealed class LibrarySearchRequest
{
    public string Query { get; set; } = string.Empty;
    public LibraryMediaType? MediaType { get; set; }

    /// <summary>Provider names to fan out to (e.g. "AniList", "MangaDex"). Empty/null means all enabled providers.</summary>
    public List<string>? Providers { get; set; }
}
