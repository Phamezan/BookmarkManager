namespace BookmarkManager.Contracts;

public record StartUrlMigrationRequest(string DeadHost);   // host only, e.g. "flamecomics.xyz"

public class UrlMigrationStatusDto
{
    public bool IsRunning { get; set; }
    public Guid? RunId { get; set; }
    public string? DeadHost { get; set; }
    public int TotalFound { get; set; }
    public int Processed { get; set; }
    public int Resolved { get; set; }
    public int Unresolved { get; set; }
    public string? CurrentBookmarkTitle { get; set; }
    public string? ErrorMessage { get; set; }
}

public class UrlMigrationProposalDto
{
    public Guid Id { get; set; }
    public Guid BookmarkId { get; set; }
    public string BookmarkTitle { get; set; } = string.Empty;
    public string OldUrl { get; set; } = string.Empty;
    public string? ProposedUrl { get; set; }
    public string? ProposedHost { get; set; }
    public string? SeriesName { get; set; }
    public string? ChapterNumber { get; set; }
    public string Confidence { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public record DecideProposalsRequest(List<Guid> ProposalIds);         // approve or reject
public record SetManualProposalUrlRequest(string Url);                // manual URL entry for an Unresolved proposal
public record DecideProposalsResponse(int Succeeded, int Failed, List<string> Errors);

public class DeadDomainCandidateDto      // for the "detected dead domains" panel
{
    public string Host { get; set; } = string.Empty;
    public int BookmarkCount { get; set; }
}
