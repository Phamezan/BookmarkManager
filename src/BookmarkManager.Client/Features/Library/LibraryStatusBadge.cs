namespace BookmarkManager.Client.Features.Library;

/// <summary>Normalizes the per-provider status vocabulary (AniList/Kitsu enums, Novelfire free text)
/// into one badge label + color. Unknown/empty statuses never guess - no badge.</summary>
public static class LibraryStatusBadge
{
    public readonly record struct Badge(string Label, string CssClass);

    public static Badge? Normalize(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return null;

        return status.Trim().ToLowerInvariant() switch
        {
            "releasing" or "current" or "ongoing" or "publishing" => new Badge("Ongoing", "lib-status-ongoing"),
            "finished" or "completed" => new Badge("Completed", "lib-status-completed"),
            "hiatus" => new Badge("Hiatus", "lib-status-hiatus"),
            "cancelled" or "canceled" => new Badge("Cancelled", "lib-status-cancelled"),
            _ => null
        };
    }
}
