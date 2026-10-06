namespace BookmarkManager.Contracts;

/// <summary>
/// Connectivity snapshot for the LAN Suwayomi server. Never throws to the caller: an unreachable
/// server surfaces as <see cref="Reachable"/> = false.
/// </summary>
public class SuwayomiStatusDto
{
    public bool Reachable { get; set; }
    public string? Version { get; set; }
    public int SourceCount { get; set; }

    /// <summary>Configured source search order, so the client can render the read-only order line.</summary>
    public List<string> SearchOrder { get; set; } = [];
}

public class SuwayomiImportPreviewDto
{
    public string FolderTitle { get; set; } = string.Empty;
    public int Total { get; set; }
    public int AlreadyLinked { get; set; }
    public int PendingReview { get; set; }
    public int ToImport { get; set; }
}

/// <summary>Shape mirrors <see cref="UrlMigrationStatusDto"/> for the import run.</summary>
public class SuwayomiImportStatusDto
{
    public bool IsRunning { get; set; }
    public Guid? RunId { get; set; }
    public string FolderTitle { get; set; } = string.Empty;
    public int TotalFound { get; set; }
    public int Processed { get; set; }
    public int Matched { get; set; }
    public int AutoApplied { get; set; }
    public int NeedsReview { get; set; }
    public int NotFound { get; set; }
    public string? CurrentTitle { get; set; }
    public string? CurrentSource { get; set; }
    public string? ErrorMessage { get; set; }
}

public record StartSuwayomiImportRequest(Guid FolderId);

/// <summary>Re-point a pending proposal at a manga the user picked manually, then approve it.</summary>
public record SuwayomiMatchRequest(int MangaId, string SourceName, string Title);

public class SuwayomiSearchResultDto
{
    public int MangaId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public bool InLibrary { get; set; }
}
