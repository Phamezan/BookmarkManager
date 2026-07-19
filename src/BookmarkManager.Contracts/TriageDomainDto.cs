namespace BookmarkManager.Contracts;

public record TriageDomainRequest(
    string MatchBaseUrl,
    string ActionType, // only "ManualFolder" is supported
    string FolderName
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
