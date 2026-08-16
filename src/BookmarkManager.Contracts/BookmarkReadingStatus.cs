namespace BookmarkManager.Contracts;

/// <summary>
/// Manager-owned personal reading/watching lifecycle status values stored in <c>BookmarkNode.Status</c>.
/// Persisted tokens: Ongoing, PlanToRead, Completed, Dropped.
/// Backward-compatible aliases: Reading -> Ongoing, Later / "Plan to Read" -> PlanToRead.
/// </summary>
public static class BookmarkReadingStatus
{
    /// <summary>Canonical storage/wire token for Ongoing series.</summary>
    public const string Ongoing = "Ongoing";

    /// <summary>Legacy alias for Ongoing, preserved for data/source compatibility.</summary>
    public const string Reading = "Reading";

    /// <summary>Canonical persisted storage/wire token for Plan to Read.</summary>
    public const string PlanToRead = "PlanToRead";

    /// <summary>Canonical storage/wire token for Completed series.</summary>
    public const string Completed = "Completed";

    /// <summary>Canonical storage/wire token for Dropped series.</summary>
    public const string Dropped = "Dropped";

    /// <summary>Legacy alias for PlanToRead, used in older records.</summary>
    public const string Later = "Later";

    /// <summary>Display and folder name for PlanToRead status folders.</summary>
    public const string PlanToReadFolder = "Plan to Read";

    public static readonly IReadOnlyList<string> All = [Ongoing, PlanToRead, Completed, Dropped];

    public static readonly IReadOnlyList<string> StatusFolderNames = [PlanToReadFolder, Completed, Dropped];

    /// <summary>
    /// Normalizes any status input (case-insensitive, trims, maps legacy aliases and display labels)
    /// into one of the 4 canonical persisted tokens: Ongoing, PlanToRead, Completed, Dropped.
    /// Returns null if not a recognized status.
    /// </summary>
    public static string? Normalize(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
            return null;

        var trimmed = status.Trim();
        if (string.Equals(trimmed, Ongoing, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, Reading, StringComparison.OrdinalIgnoreCase))
            return Ongoing;

        if (string.Equals(trimmed, PlanToRead, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, PlanToReadFolder, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, Later, StringComparison.OrdinalIgnoreCase))
            return PlanToRead;

        if (string.Equals(trimmed, Completed, StringComparison.OrdinalIgnoreCase))
            return Completed;

        if (string.Equals(trimmed, Dropped, StringComparison.OrdinalIgnoreCase))
            return Dropped;

        return null;
    }

    public static bool IsValid(string? status) => Normalize(status) is not null;

    public static bool IsPlanToRead(string? status) => Normalize(status) == PlanToRead;

    /// <summary>
    /// Returns the target subfolder title for a given status under the category root,
    /// or null if the status projects directly to the category root (Ongoing/Reading).
    /// </summary>
    public static string? GetStatusFolderName(string? status)
    {
        var norm = Normalize(status);
        return norm switch
        {
            PlanToRead => PlanToReadFolder,
            Completed => Completed,
            Dropped => Dropped,
            _ => null
        };
    }

    /// <summary>
    /// Returns the human-readable UI display label for a status token.
    /// </summary>
    public static string GetDisplayLabel(string? status)
    {
        var norm = Normalize(status);
        return norm switch
        {
            PlanToRead => PlanToReadFolder,
            Ongoing => Ongoing,
            Completed => Completed,
            Dropped => Dropped,
            _ => status ?? Ongoing
        };
    }
}
