namespace BookmarkManager.Contracts;

/// <summary>User reading-state values stored in <c>BookmarkNode.Status</c>.</summary>
public static class BookmarkReadingStatus
{
    public const string PlanToRead = "PlanToRead";
    public const string Reading = "Reading";
    public const string Completed = "Completed";
}
