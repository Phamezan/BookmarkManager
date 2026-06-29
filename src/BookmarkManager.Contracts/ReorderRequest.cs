namespace BookmarkManager.Contracts;

public class ReorderRequest
{
    public Guid Id { get; set; }
    public int NewPosition { get; set; }
}
