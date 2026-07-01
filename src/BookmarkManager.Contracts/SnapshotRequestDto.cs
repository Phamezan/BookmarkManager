namespace BookmarkManager.Contracts;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
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
