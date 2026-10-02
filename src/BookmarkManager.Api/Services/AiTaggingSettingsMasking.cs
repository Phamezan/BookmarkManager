using System.Text.Json;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services;

// Masks the AI-tagging secrets at the HTTP boundary only. AiTaggingSettingsService.GetAsync must
// keep returning the real keys for server-side consumers (tagging, URL migration, RAG, Gemini
// search); the SettingsController routes every HTTP response/request through this helper instead.
//
// Masked value: "••••" + last up-to-4 characters of the stored key, or empty when unset.
// PUT rules per secret:
//   - Clear<Name> = true                        -> clear to empty (wins over any typed value)
//   - incoming null/whitespace                   -> keep stored
//   - incoming equals the mask of the stored key -> keep stored
//   - anything else                              -> replace with the incoming trimmed value
public static class AiTaggingSettingsMasking
{
    public const string MaskPrefix = "\u2022\u2022\u2022\u2022";

    public const string SecretApiKey = "ApiKey";
    public const string SecretGroqApiKey = "GroqApiKey";
    public const string SecretGeminiApiKey = "GeminiApiKey";
    public const string SecretTavilyApiKey = "TavilyApiKey";
    public const string SecretRagApiKey = "RagApiKey";
    public const string SecretRagFallbackApiKey = "RagFallbackApiKey";

    private static readonly JsonSerializerOptions CloneOptions = new(JsonSerializerDefaults.Web);

    // Short secrets (<= 8 chars) are masked completely: revealing "last 4" of a short secret would
    // hand over most or all of it. Longer secrets expose only the last 4 characters.
    public const int MinLengthForTail = 9;

    public static string Mask(string? secret)
    {
        if (string.IsNullOrEmpty(secret))
            return string.Empty;

        if (secret.Length < MinLengthForTail)
            return MaskPrefix;

        return MaskPrefix + secret[^4..];
    }

    public static bool IsMaskedValue(string? value, string? storedSecret)
        => !string.IsNullOrEmpty(value) && string.Equals(value, Mask(storedSecret), StringComparison.Ordinal);

    public static bool HasSecret(string? secret) => !string.IsNullOrWhiteSpace(secret);

    // Returns a copy safe to serialize over HTTP: real keys replaced with masks, Has<Name> set from
    // the real values, Clear<Name> reset so stale client flags never round-trip.
    public static AiTaggingSettingsDto ToMasked(AiTaggingSettingsDto source)
    {
        var masked = Copy(source);
        masked.HasApiKey = HasSecret(source.ApiKey);
        masked.HasGroqApiKey = HasSecret(source.GroqApiKey);
        masked.HasGeminiApiKey = HasSecret(source.GeminiApiKey);
        masked.HasTavilyApiKey = HasSecret(source.TavilyApiKey);
        masked.HasRagApiKey = HasSecret(source.RagApiKey);
        masked.HasRagFallbackApiKey = HasSecret(source.RagFallbackApiKey);

        masked.ApiKey = Mask(source.ApiKey);
        masked.GroqApiKey = Mask(source.GroqApiKey);
        masked.GeminiApiKey = Mask(source.GeminiApiKey);
        masked.TavilyApiKey = Mask(source.TavilyApiKey);
        masked.RagApiKey = Mask(source.RagApiKey);
        masked.RagFallbackApiKey = Mask(source.RagFallbackApiKey);

        masked.ClearApiKey = false;
        masked.ClearGroqApiKey = false;
        masked.ClearGeminiApiKey = false;
        masked.ClearTavilyApiKey = false;
        masked.ClearRagApiKey = false;
        masked.ClearRagFallbackApiKey = false;
        return masked;
    }

    // Returns a human-readable message when a preserved secret's destination URL is being changed,
    // or null when the save is allowed. A preserved secret (masked/empty/null incoming) may not be
    // re-pointed at a different endpoint: the stored key would silently start flowing to that new
    // host from tagging/migration/RAG. Typed new keys, explicit clears, and URL changes with no
    // stored key are all allowed.
    //
    // Every actual key -> URL pairing found by grepping the server consumers is covered:
    //   ApiKey           -> BaseUrl (OpenRouterSeriesIdentificationClient) and Endpoint (the legacy
    //                       GeminiSeriesIdentificationClient in AiSeriesIdentifierService)
    //   GeminiApiKey     -> Endpoint (GeminiGroundedSearchService)
    //   GroqApiKey       -> GroqBaseUrl (Groq identify, GroqSeriesExtractionService, GroqCompoundSearchService)
    //   TavilyApiKey     -> none: the Tavily endpoint is a fixed constant (TavilySearchService.ApiEndpoint),
    //                       so there is no user-editable URL to re-point the key at.
    //   RagApiKey        -> RagBaseUrl (LibraryRagService)
    //   RagFallbackApiKey-> RagFallbackBaseUrl (LibraryRagService)
    public static string? FindEndpointChangeViolation(AiTaggingSettingsDto incoming, AiTaggingSettingsDto stored)
        => CheckPreservedEndpointChange(incoming.ApiKey, stored.ApiKey, incoming.ClearApiKey, "OpenRouter",
                (incoming.BaseUrl, stored.BaseUrl, "https://openrouter.ai/api/v1"),
                (incoming.Endpoint, stored.Endpoint, "https://generativelanguage.googleapis.com/v1beta"))
           ?? CheckPreservedEndpointChange(incoming.GeminiApiKey, stored.GeminiApiKey, incoming.ClearGeminiApiKey, "Gemini",
                (incoming.Endpoint, stored.Endpoint, "https://generativelanguage.googleapis.com/v1beta"))
           ?? CheckPreservedEndpointChange(incoming.GroqApiKey, stored.GroqApiKey, incoming.ClearGroqApiKey, "Groq",
                (incoming.GroqBaseUrl, stored.GroqBaseUrl, "https://api.groq.com/openai/v1"))
           ?? CheckPreservedEndpointChange(incoming.RagApiKey, stored.RagApiKey, incoming.ClearRagApiKey, "Library assistant",
                (incoming.RagBaseUrl, stored.RagBaseUrl, "https://api.groq.com/openai/v1"))
           ?? CheckPreservedEndpointChange(incoming.RagFallbackApiKey, stored.RagFallbackApiKey, incoming.ClearRagFallbackApiKey, "fallback",
                (incoming.RagFallbackBaseUrl, stored.RagFallbackBaseUrl, "https://integrate.api.nvidia.com/v1"));

    private static string? CheckPreservedEndpointChange(
        string? incomingKey,
        string? storedKey,
        bool clear,
        string displayName,
        params (string? Incoming, string? Stored, string Fallback)[] endpoints)
    {
        if (clear)
            return null;
        if (string.IsNullOrWhiteSpace(storedKey))
            return null;
        if (!string.IsNullOrWhiteSpace(incomingKey) && !IsMaskedValue(incomingKey, storedKey))
            return null;

        foreach (var (incomingUrl, storedUrl, fallback) in endpoints)
        {
            if (!SameUrl(incomingUrl, storedUrl, fallback))
                return $"Re-enter the {displayName} API key when changing its endpoint.";
        }

        return null;
    }

    // Rejects any incoming value that looks like a mask (starts with the mask prefix) but is not
    // this secret's own mask. Without this, pasting secret A's mask into secret B would be treated
    // as a typed key and stored verbatim, corrupting B. Never stores the literal mask.
    public static string? FindMaskInjectionViolation(AiTaggingSettingsDto incoming, AiTaggingSettingsDto stored)
        => CheckMaskInjection(incoming.ApiKey, stored.ApiKey, "OpenRouter")
           ?? CheckMaskInjection(incoming.GroqApiKey, stored.GroqApiKey, "Groq")
           ?? CheckMaskInjection(incoming.GeminiApiKey, stored.GeminiApiKey, "Gemini")
           ?? CheckMaskInjection(incoming.TavilyApiKey, stored.TavilyApiKey, "Tavily")
           ?? CheckMaskInjection(incoming.RagApiKey, stored.RagApiKey, "Library assistant")
           ?? CheckMaskInjection(incoming.RagFallbackApiKey, stored.RagFallbackApiKey, "fallback");

    private static string? CheckMaskInjection(string? incoming, string? storedKey, string displayName)
    {
        if (string.IsNullOrEmpty(incoming) || !incoming.StartsWith(MaskPrefix, StringComparison.Ordinal))
            return null;
        if (IsMaskedValue(incoming, storedKey))
            return null;

        return $"Invalid masked value submitted for the {displayName} API key. Type a new key or clear it.";
    }

    private static bool SameUrl(string? left, string? right, string fallback)
        => string.Equals(NormalizeUrl(left, fallback), NormalizeUrl(right, fallback), StringComparison.OrdinalIgnoreCase);

    // Trailing slashes and host/scheme case are insignificant; the path and port still matter.
    private static string NormalizeUrl(string? url, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(url) ? fallback : url.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return value.TrimEnd('/');

        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}{port}{uri.AbsolutePath.TrimEnd('/')}{uri.Query}";
    }

    // Produces the DTO to persist, carrying the stored key forward for any secret the caller did
    // not explicitly replace or clear.
    public static AiTaggingSettingsDto MergeSecrets(AiTaggingSettingsDto incoming, AiTaggingSettingsDto stored)
    {
        var merged = Copy(incoming);

        merged.ApiKey = Resolve(incoming.ApiKey, stored.ApiKey, incoming.ClearApiKey);
        merged.GroqApiKey = Resolve(incoming.GroqApiKey, stored.GroqApiKey, incoming.ClearGroqApiKey);
        merged.GeminiApiKey = Resolve(incoming.GeminiApiKey, stored.GeminiApiKey, incoming.ClearGeminiApiKey);
        merged.TavilyApiKey = Resolve(incoming.TavilyApiKey, stored.TavilyApiKey, incoming.ClearTavilyApiKey);
        merged.RagApiKey = Resolve(incoming.RagApiKey, stored.RagApiKey, incoming.ClearRagApiKey);
        merged.RagFallbackApiKey = Resolve(incoming.RagFallbackApiKey, stored.RagFallbackApiKey, incoming.ClearRagFallbackApiKey);

        merged.HasApiKey = false;
        merged.HasGroqApiKey = false;
        merged.HasGeminiApiKey = false;
        merged.HasTavilyApiKey = false;
        merged.HasRagApiKey = false;
        merged.HasRagFallbackApiKey = false;
        merged.ClearApiKey = false;
        merged.ClearGroqApiKey = false;
        merged.ClearGeminiApiKey = false;
        merged.ClearTavilyApiKey = false;
        merged.ClearRagApiKey = false;
        merged.ClearRagFallbackApiKey = false;
        return merged;
    }

    private static string Resolve(string? incoming, string? stored, bool clear)
    {
        if (clear)
            return string.Empty;
        if (string.IsNullOrWhiteSpace(incoming))
            return stored?.Trim() ?? string.Empty;
        // Any mask-shaped value (own mask, or a rejected cross-secret mask that slipped past
        // validation) is never persisted verbatim - keep what is stored.
        if (incoming.StartsWith(MaskPrefix, StringComparison.Ordinal))
            return stored?.Trim() ?? string.Empty;
        return incoming.Trim();
    }

    // Picks the stored key the caller means when a test-connection request omits the real key.
    public static string? StoredSecretFor(AiTaggingSettingsDto stored, string? secretName, string? provider)
        => secretName switch
        {
            SecretGroqApiKey => stored.GroqApiKey,
            SecretGeminiApiKey => stored.GeminiApiKey,
            SecretTavilyApiKey => stored.TavilyApiKey,
            SecretRagApiKey => stored.RagApiKey,
            SecretRagFallbackApiKey => stored.RagFallbackApiKey,
            SecretApiKey => stored.ApiKey,
            _ => string.Equals(provider, "Groq", StringComparison.OrdinalIgnoreCase)
                ? stored.GroqApiKey
                : stored.ApiKey
        };

    // Normalizes an absent/unknown SecretName to the concrete secret the request targets, matching
    // the legacy Provider-based mapping used before SecretName existed.
    public static string ResolveSecretName(string? secretName, string? provider)
        => secretName switch
        {
            SecretApiKey or SecretGroqApiKey or SecretGeminiApiKey or SecretTavilyApiKey or SecretRagApiKey or SecretRagFallbackApiKey => secretName,
            _ => string.Equals(provider, "Groq", StringComparison.OrdinalIgnoreCase) ? SecretGroqApiKey : SecretApiKey
        };

    // The stored destination that owns each secret. When a stored key is used the request MUST be
    // pinned here; the caller-supplied BaseUrl is ignored so a LAN caller cannot redirect a stored
    // key to an attacker-controlled host. Falls back to the built-in default when the stored URL is
    // blank (mirrors AiTaggingSettingsService.DefaultSettings).
    public static string StoredBaseUrlFor(AiTaggingSettingsDto stored, string secretName)
        => secretName switch
        {
            SecretGroqApiKey => FirstNonEmpty(stored.GroqBaseUrl, "https://api.groq.com/openai/v1"),
            SecretGeminiApiKey => FirstNonEmpty(stored.Endpoint, "https://generativelanguage.googleapis.com/v1beta"),
            // Fixed endpoint (no user-editable URL): a stored Tavily key is always pinned here.
            SecretTavilyApiKey => UrlMigration.TavilySearchService.ApiBaseUrl,
            SecretRagApiKey => FirstNonEmpty(stored.RagBaseUrl, "https://api.groq.com/openai/v1"),
            SecretRagFallbackApiKey => FirstNonEmpty(stored.RagFallbackBaseUrl, "https://integrate.api.nvidia.com/v1"),
            _ => FirstNonEmpty(stored.BaseUrl, "https://openrouter.ai/api/v1")
        };

    // The client/provider that owns each secret, so a stored key is never routed through the
    // caller's chosen provider path either.
    public static string StoredProviderFor(string secretName, string? provider)
        => secretName switch
        {
            SecretApiKey => "OpenRouter",
            SecretGroqApiKey or SecretRagApiKey or SecretRagFallbackApiKey => "Groq",
            SecretGeminiApiKey => "Gemini",
            SecretTavilyApiKey => "Tavily",
            _ => provider ?? "OpenRouter"
        };

    // Substitutes the stored key when the request carries nothing usable, so the browser never has
    // to hold the real secret to run a test. When a stored key is used the destination (BaseUrl)
    // and provider are pinned to the stored values for that secret - the caller's BaseUrl is
    // deliberately ignored. A typed new key keeps the caller's BaseUrl/Provider.
    public static TestAiKeyRequest MergeTestSecret(TestAiKeyRequest request, AiTaggingSettingsDto stored)
    {
        var incoming = request.ApiKey;
        if (!string.IsNullOrWhiteSpace(incoming))
        {
            var storedForKey = StoredSecretFor(stored, request.SecretName, request.Provider);
            if (!IsMaskedValue(incoming, storedForKey))
                return request;
        }

        var secretName = ResolveSecretName(request.SecretName, request.Provider);
        return new TestAiKeyRequest
        {
            BaseUrl = StoredBaseUrlFor(stored, secretName),
            Model = request.Model,
            ApiKey = StoredSecretFor(stored, secretName, request.Provider) ?? string.Empty,
            Provider = StoredProviderFor(secretName, request.Provider),
            SecretName = secretName
        };
    }

    private static string FirstNonEmpty(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().TrimEnd('/');

    private static AiTaggingSettingsDto Copy(AiTaggingSettingsDto source)
        => JsonSerializer.Deserialize<AiTaggingSettingsDto>(JsonSerializer.Serialize(source, CloneOptions), CloneOptions)
           ?? new AiTaggingSettingsDto();
}
