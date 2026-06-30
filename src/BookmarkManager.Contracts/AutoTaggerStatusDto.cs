namespace BookmarkManager.Contracts;

public sealed class AutoTaggerStatusDto
{
    public bool IsRunning { get; set; }
    public DateTime? LastStartedAt { get; set; }
    public DateTime? LastCompletedAt { get; set; }
    public string? LastError { get; set; }
    public RetagAllResult LastResult { get; set; } = new();
}
