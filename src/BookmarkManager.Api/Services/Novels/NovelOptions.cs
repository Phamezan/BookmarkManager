namespace BookmarkManager.Api.Services.Novels;

public sealed class NovelOptions
{
    public const string SectionName = "Novels";

    public string SidecarUrl { get; set; } = "http://novels:3000";

    /// <summary>Comma-separated list of enabled sidecar source ids (e.g. "hangulplanet").</summary>
    public string EnabledSources { get; set; } = "hangulplanet";

    /// <summary>Enables Royal Road review lookup by exact normalized title match.</summary>
    public bool RoyalRoadReviews { get; set; } = true;

    public HashSet<string> GetEnabledSourceSet()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(EnabledSources))
        {
            return result;
        }

        foreach (var token in EnabledSources.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Length > 0)
            {
                result.Add(token.ToLowerInvariant());
            }
        }

        return result;
    }
}
