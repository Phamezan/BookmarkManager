namespace BookmarkManager.Client.Services;

public sealed record FolderCandidateDto(
    string BrowserNodeId,
    string? ParentBrowserNodeId,
    string Title,
    int Position,
    bool IsProtected,
    bool IsTracked,
    DateTime CapturedAt);
