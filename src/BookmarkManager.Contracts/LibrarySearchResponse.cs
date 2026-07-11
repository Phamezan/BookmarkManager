namespace BookmarkManager.Contracts;

public enum LibraryProviderResultStatus
{
    Ok,
    Failed,
    Timeout,
    Disabled
}

public sealed record LibraryProviderStatusDto(string Provider, LibraryProviderResultStatus Status, string? Error);

public sealed class LibrarySearchResponse
{
    public List<LibraryEntryDto> Items { get; set; } = [];
    public List<LibraryProviderStatusDto> ProviderStatuses { get; set; } = [];

    /// <summary>Total number of catalog rows matching the request (paged endpoints only, e.g. trending);
    /// lets the client show "N titles" and decide whether to offer "Load more".</summary>
    public int TotalCount { get; set; }

    /// <summary>True when more pages remain beyond this response's <see cref="Items"/> (paged endpoints only).</summary>
    public bool HasMore { get; set; }
}
