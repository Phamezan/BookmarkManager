namespace BookmarkManager.Client.Services.AutoTagging;

public static class AiAutoTagSummaryMessageFilter
{
    private static readonly string[] SuppressedPrefixes =
    [
        "Prefetching provider tags",
        "Deterministic pass:",
        "AI pass:",
        "Found ",
        "Processing next ",
        "Finished with ",
        "Run ",
        "Provider timing"
    ];

    public static bool ShouldDisplay(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        foreach (var prefix in SuppressedPrefixes)
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
                return false;
        }

        return true;
    }
}
