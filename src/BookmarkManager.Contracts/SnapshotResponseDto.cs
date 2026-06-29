namespace BookmarkManager.Contracts;

public sealed class SnapshotResponseDto
{
    public Guid RequestId { get; set; }
    public DateTime AcceptedAt { get; set; }
    public List<NodeMappingDto> Mappings { get; set; } = [];
}
