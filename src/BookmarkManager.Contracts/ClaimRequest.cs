namespace BookmarkManager.Contracts;

public sealed class ClaimRequest
{
    public int ConfigVersion { get; set; }
    public int MaxCommands { get; set; }
}
