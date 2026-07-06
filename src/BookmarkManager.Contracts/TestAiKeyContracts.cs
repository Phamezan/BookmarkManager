namespace BookmarkManager.Contracts;

// Sent from the Settings page to verify the OpenRouter key + model the user has typed in,
// before saving. Carries the in-form values so a bad key can be caught without persisting it.
public class TestAiKeyRequest
{
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string Model { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
}

public class TestAiKeyResponse
{
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public string Message { get; set; } = string.Empty;
}
