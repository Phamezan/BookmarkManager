namespace BookmarkManager.Contracts;

// Sent from the Settings page to verify the OpenRouter key + model the user has typed in,
// before saving. Carries the in-form values so a bad key can be caught without persisting it.
public class TestAiKeyRequest
{
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string Model { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;

    // "OpenRouter" (default) or "Groq" - selects which provider's test logic runs the request.
    public string Provider { get; set; } = "OpenRouter";

    // Which stored secret to fall back to when ApiKey is empty or still the masked value from GET.
    // One of: ApiKey, GroqApiKey, GeminiApiKey, RagApiKey, RagFallbackApiKey. When null/unknown the
    // server falls back to Provider ("Groq" -> GroqApiKey, otherwise ApiKey) for backwards compatibility.
    public string? SecretName { get; set; }
}

public class TestAiKeyResponse
{
    public bool Success { get; set; }
    public int StatusCode { get; set; }
    public string Message { get; set; } = string.Empty;
}
