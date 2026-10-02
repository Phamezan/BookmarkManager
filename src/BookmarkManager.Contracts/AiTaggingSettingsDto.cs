namespace BookmarkManager.Contracts;

public class AiTaggingSettingsDto
{
    public AiTaggingSettingsDto Clone() => (AiTaggingSettingsDto)MemberwiseClone();

    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://openrouter.ai/api/v1";
    public string Model { get; set; } = "nvidia/nemotron-3-ultra-550b-a55b:free";
    public string? ApiKey { get; set; } = string.Empty;

    // HTTP-boundary secret handling: GET always masks ApiKey (never the real value) and sets this
    // flag so the client can show "saved". PUT keeps the stored key when ApiKey is empty/whitespace
    // or still equals the mask; ClearApiKey forces it to empty. These flags are never persisted
    // (AiTaggingSettingsService.Normalize drops them) and are meaningless to server-side consumers.
    public bool HasApiKey { get; set; }
    public bool ClearApiKey { get; set; }

    public int RequestsPerMinute { get; set; } = 15;
    public int PreferredBatchSize { get; set; } = 25;

    // Fallback provider used when OpenRouter's daily free-tier quota is exhausted (not just RPM throttled).
    public string? GroqApiKey { get; set; } = string.Empty;
    public bool HasGroqApiKey { get; set; }
    public bool ClearGroqApiKey { get; set; }
    public string GroqModel { get; set; } = "llama-3.3-70b-versatile";
    public string GroqBaseUrl { get; set; } = "https://api.groq.com/openai/v1";
    public int GroqRequestsPerMinute { get; set; } = 25;

    // URL Migrator v2: model used for the Groq search/rerank stage (Groq compound performs live web search).
    public string MigrationSearchModel { get; set; } = "groq/compound-mini";

    // URL Migrator v2 search provider. "Gemini" uses Google Search grounding (the default, since
    // Groq's compound models were decommissioned on 2026-09-21); "Groq" keeps the Compound path.
    // Missing from older persisted settings JSON, so the property initializer must supply the
    // retired-Compound-safe default for existing installs too.
    public string MigrationSearchProvider { get; set; } = "Gemini";
    public string? GeminiApiKey { get; set; } = string.Empty;
    public bool HasGeminiApiKey { get; set; }
    public bool ClearGeminiApiKey { get; set; }
    public string GeminiSearchModel { get; set; } = "gemini-3.8-flash";

    // When enabled, High-confidence migration proposals are auto-approved right after creation.
    public bool MigrationAutoApproveHigh { get; set; } = false;

    // Library RAG assistant: OpenAI-compatible chat model that answers grounded on retrieved catalog
    // entries. Defaults to the same Groq endpoint/model shape as the tagging fallback.
    public string RagModel { get; set; } = "llama-3.3-70b-versatile";
    public string? RagApiKey { get; set; } = string.Empty;
    public bool HasRagApiKey { get; set; }
    public bool ClearRagApiKey { get; set; }
    public string RagBaseUrl { get; set; } = "https://api.groq.com/openai/v1";
    public int RagRequestsPerMinute { get; set; } = 30;

    // Optional secondary provider tried automatically when the primary is rate-limited (429) or errors.
    // Defaults to NVIDIA-hosted chat so a different account/quota absorbs the overflow. Blank key = disabled.
    public string? RagFallbackApiKey { get; set; } = string.Empty;
    public bool HasRagFallbackApiKey { get; set; }
    public bool ClearRagFallbackApiKey { get; set; }
    public string RagFallbackModel { get; set; } = "meta/llama-3.3-70b-instruct";
    public string RagFallbackBaseUrl { get; set; } = "https://integrate.api.nvidia.com/v1";

    // Persona/system prompt prepended to every Library assistant chat. Editable so the user can shape
    // the assistant's voice and expertise. Grounding rules are appended by the server regardless.
    public string RagSystemPrompt { get; set; } = RagDefaultSystemPrompt;

    public const string RagDefaultSystemPrompt =
        "You are an expert anime, manga, manhwa, manhua, and light/web novel curator with deep knowledge "
        + "of genres, tropes, authors, and reader tastes. You are warm, concise, and opinionated, and you "
        + "recommend titles the way a passionate friend would.";
}
