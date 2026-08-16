using System.Text.Json.Serialization;

namespace BookmarkManager.Contracts;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BookmarkStatusOutcome
{
    Changed,
    AlreadyCorrect,
    Skipped,
    Failed
}

public class BookmarkStatusItemResult
{
    public Guid BookmarkId { get; set; }
    public BookmarkStatusOutcome Outcome { get; set; }
    public string? PreviousStatus { get; set; }
    public string? CurrentStatus { get; set; }
    public string? Message { get; set; }
    public BookmarkNodeDto? Node { get; set; }
}

public class BulkUpdateBookmarkStatusResponse
{
    public List<BookmarkStatusItemResult> Results { get; set; } = [];
    public int TotalCount => Results.Count;
    public int ChangedCount => Results.Count(r => r.Outcome == BookmarkStatusOutcome.Changed);
    public int AlreadyCorrectCount => Results.Count(r => r.Outcome == BookmarkStatusOutcome.AlreadyCorrect);
    public int SkippedCount => Results.Count(r => r.Outcome == BookmarkStatusOutcome.Skipped);
    public int FailedCount => Results.Count(r => r.Outcome == BookmarkStatusOutcome.Failed);
}
