namespace BookmarkManager.Contracts;

public record TriageDomainRequest(
    string MatchBaseUrl,
    string ActionType, // "ManualFolder" or "AutoSearch"
    string FolderName
);

public record TriageDomainResponse(
    int TotalFound,
    int SuccessfullyProcessed,
    string TargetFolder
);

public class TriageJobStatusDto
{
    public bool IsRunning { get; set; }
    public int TotalFound { get; set; }
    public int SuccessfullyProcessed { get; set; }
    public string? TargetFolder { get; set; }
    public string? CurrentDomain { get; set; }
    public string? ErrorMessage { get; set; }
}
