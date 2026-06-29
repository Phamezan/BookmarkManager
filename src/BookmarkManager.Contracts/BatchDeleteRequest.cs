namespace BookmarkManager.Contracts;

public class BatchDeleteRequest
{
    public List<Guid> Ids { get; set; } = [];
}
