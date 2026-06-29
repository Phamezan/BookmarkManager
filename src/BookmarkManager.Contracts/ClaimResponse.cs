namespace BookmarkManager.Contracts;

public sealed class ClaimResponse
{
    public List<ExtensionCommandDto> Commands { get; set; } = [];
}
