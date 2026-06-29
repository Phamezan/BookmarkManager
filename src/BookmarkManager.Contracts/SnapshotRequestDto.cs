namespace BookmarkManager.Contracts;

public enum SnapshotReason
{
    InitialImport,
    Repair,
    ImportCompleted
}

public sealed class SnapshotRequestDto
{
    public Guid RequestId { get; set; }
    public SnapshotReason Reason { get; set; }
}
