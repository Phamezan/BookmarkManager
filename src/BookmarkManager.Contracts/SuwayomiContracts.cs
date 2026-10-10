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

/// <summary>
/// Batched library-membership query from the WebUI add-on: titles it is about to show, matched
/// against the user's Suwayomi library by normalized title and alternative title. Max 200 per call.
/// </summary>
public class SuwayomiLibraryMatchRequest
{
    public List<string> Titles { get; set; } = [];
}

/// <summary>
/// One matched title: the text as sent, plus the library series' identity and reading progress, so
/// the WebUI add-on can badge a cross-source duplicate and link to the library copy.
/// </summary>
public class SuwayomiLibraryMatchDto
{
    public string Title { get; set; } = string.Empty;
    public int MangaId { get; set; }
    public int Read { get; set; }
    public int Latest { get; set; }

    /// <summary>The matched library series' title (may differ from <see cref="Title"/> via an alias).</summary>
    public string LibraryTitle { get; set; } = string.Empty;

    /// <summary>Display name of the source the library series belongs to.</summary>
    public string? SourceName { get; set; }
}
