namespace BookmarkManager.Contracts;

public sealed class SnapshotRootPayloadDto
{
    public Guid TrackedRootId { get; set; }
    public BookmarkNodeDto Root { get; set; } = null!;
}

public sealed class SnapshotRequestPayloadDto
{
    public Guid RequestId { get; set; }
    public int ConfigVersion { get; set; }
    public DateTime CapturedAt { get; set; }
    public List<SnapshotRootPayloadDto> Roots { get; set; } = [];
}
