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
