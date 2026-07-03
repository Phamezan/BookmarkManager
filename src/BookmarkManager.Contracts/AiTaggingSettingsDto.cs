namespace BookmarkManager.Contracts;

public class AiTaggingSettingsDto
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string Model { get; set; } = "google/gemini-2.5-flash:free";
    public string ApiKey { get; set; } = string.Empty;
    public int RequestsPerMinute { get; set; } = 15;
    public int PreferredBatchSize { get; set; } = 25;
}
