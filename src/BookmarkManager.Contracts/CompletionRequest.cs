namespace BookmarkManager.Contracts;

public sealed class CompletionRequest
{
    public Guid LeaseId { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? BrowserNodeId { get; set; }
    public List<NodeMappingDto> CompletedNodeMappings { get; set; } = [];
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
}
