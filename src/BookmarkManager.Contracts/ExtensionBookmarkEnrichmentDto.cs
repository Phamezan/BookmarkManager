namespace BookmarkManager.Contracts;

/// <summary>Extension toast enrichment after a Brave bookmark create syncs to the API.</summary>
public sealed record ExtensionBookmarkEnrichmentDto(
    Guid Id,
    string Title,
    string? FolderPath,
    IReadOnlyList<string> Tags,
    string? Status,
    string? CoverImageUrl);
