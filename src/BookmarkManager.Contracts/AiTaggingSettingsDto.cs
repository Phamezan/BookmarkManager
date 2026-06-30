namespace BookmarkManager.Contracts;

public class AiTaggingSettingsDto
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}
