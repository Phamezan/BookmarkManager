namespace BookmarkManager.Client.Features.Library;

/// <summary>Builds the "{provider}:{providerId}" lookup key used to join reading-progress rows to
/// catalog cards, case-insensitively - same identity semantics as <c>LibraryRecommends.SameSeries</c>.</summary>
public static class LibraryReadingProgressKey
{
    public static string Build(string provider, string providerId) => $"{provider}:{providerId}".ToLowerInvariant();
}
