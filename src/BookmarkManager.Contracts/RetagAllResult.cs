namespace BookmarkManager.Contracts;

/// <summary>
/// Result of the bulk retag-all operation returned by the API.
/// </summary>
public class RetagAllResult
{
    public int Tagged { get; set; }
    public int Skipped { get; set; }
    public int Total { get; set; }
}
