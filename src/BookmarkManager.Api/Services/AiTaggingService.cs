using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services;

/// <summary>
/// Optional LLM-backed tagger. Calls an OpenAI-compatible chat completions
/// endpoint (OpenAI, Ollama, OpenRouter, LM Studio, …) and asks for a small
/// JSON list of tags. Disabled when no endpoint is configured.
/// </summary>
public sealed class AiTaggingService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<AiTaggingService> _logger;
    private readonly string _settingsFilePath;
    private readonly object _lock = new();
    private AiTaggingOptions _cachedOptions = null!;

    public AiTaggingService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<AiTaggingService> logger)
    {
        _httpFactory = httpFactory;
        _config = config;
        _logger = logger;

        const string containerDataPath = "/data";
        var contentRoot = AppDomain.CurrentDomain.BaseDirectory;
        _settingsFilePath = Directory.Exists(containerDataPath)
            ? Path.Combine(containerDataPath, "ai_tagging_settings.json")
            : Path.Combine(contentRoot, "ai_tagging_settings.json");

        ReloadOptions();
    }

    public AiTaggingOptions GetCurrentOptions()
    {
        lock (_lock)
        {
            return _cachedOptions;
        }
    }

    public void SaveSettings(bool enabled, string endpoint, string model, string apiKey)
    {
        try
        {
            // If the user sends the mask, keep the existing API key
            var current = GetCurrentOptions();
            var finalApiKey = apiKey == "••••••••" ? current.ApiKey : apiKey;

            var saved = new SavedAiSettings
            {
                Enabled = enabled,
                Endpoint = endpoint,
                Model = model,
                ApiKey = finalApiKey
            };
            var json = JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_settingsFilePath, json);
            ReloadOptions();
        }
        catch (System.Exception ex)
        {
            _logger.LogError(ex, "Failed to save custom AI settings to {Path}", _settingsFilePath);
            throw;
        }
    }

    private void ReloadOptions()
    {
        lock (_lock)
        {
            var opts = AiTaggingOptions.Bind(_config.GetSection("AiTagging"));
            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    var saved = JsonSerializer.Deserialize<SavedAiSettings>(json);
                    if (saved != null)
                    {
                        opts.IsEnabled = saved.Enabled;
                        if (!string.IsNullOrWhiteSpace(saved.Endpoint)) opts.Endpoint = saved.Endpoint;
                        if (!string.IsNullOrWhiteSpace(saved.Model)) opts.Model = saved.Model;
                        if (!string.IsNullOrWhiteSpace(saved.ApiKey)) opts.ApiKey = saved.ApiKey;
                    }
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load custom AI settings from {Path}", _settingsFilePath);
            }
            _cachedOptions = opts;
        }
    }

    private class SavedAiSettings
    {
        public bool Enabled { get; set; }
        public string Endpoint { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string ApiKey { get; set; } = string.Empty;
    }

    public bool IsConfigured
    {
        get
        {
            var opts = GetCurrentOptions();
            return opts.IsEnabled
                && !string.IsNullOrWhiteSpace(opts.Endpoint)
                && !string.IsNullOrWhiteSpace(opts.Model);
        }
    }

    /// <summary>
    /// Ask the model for tags. Returns an empty list (not an error) when AI
    /// tagging is disabled, so callers can fall back to the heuristic without
    /// try/catch sprawl.
    /// </summary>
    public async Task<List<string>> SuggestTagsAsync(
        string title,
        string? url,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return new List<string>();

        var opts = GetCurrentOptions();
        var cleanUrl = string.IsNullOrWhiteSpace(url) ? "(none)" : url;
        var cleanDesc = string.IsNullOrWhiteSpace(description) ? "(none)" : description;
        var cleanTitle = string.IsNullOrWhiteSpace(title) ? "(untitled)" : title;

        var system = "You tag bookmarks. Reply with ONLY a JSON object of the form " +
                     "{\"tags\":[\"tag\",\"tag\",...]}. Use 1-6 short tags. " +
                     "Prefer concrete nouns the page is about (language, framework, " +
                     "topic, domain). Use TitleCase. No commentary, no prose.";

        var user = $"Title: {cleanTitle}\nURL: {cleanUrl}\nDescription: {cleanDesc}";

        var body = new
        {
            model = opts.Model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            },
            temperature = opts.Temperature,
            max_tokens = 256,
            response_format = new { type = "json_object" }
        };

        try
        {
            var http = _httpFactory.CreateClient(nameof(AiTaggingService));
            using var req = new HttpRequestMessage(HttpMethod.Post, opts.Endpoint) { Content = JsonContent.Create(body, options: JsonOptions) };
            if (!string.IsNullOrWhiteSpace(opts.ApiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);

            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            http.Timeout = opts.Timeout;

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            var raw = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseTags(raw);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Request-level timeout (SendAsync honors SendAsync's token, but the
            // inner HttpClient.Timeout surfaces as TaskCanceledException).
            _logger.LogWarning("AI tagging timed out for {Url}", cleanUrl);
            return new List<string>();
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "AI tagging failed for {Url}; falling back to heuristic.", cleanUrl);
            return new List<string>();
        }
    }

    public async Task<Dictionary<Guid, List<string>>> SuggestTagsBatchAsync(
        List<BookmarkTagCandidateDto> items,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, List<string>>();
        if (!IsConfigured || items.Count == 0)
            return result;

        var opts = GetCurrentOptions();

        var system = "You tag bookmarks. You are given a JSON array of bookmarks containing " +
                     "{\"id\":\"...\",\"title\":\"...\",\"url\":\"...\"}. " +
                     "Reply with ONLY a JSON object of the form " +
                     "{\"tags\":{\"<id>\":[\"tag\",\"tag\"],\"<id>\":[...]}}. " +
                     "Use 1-6 short tags per bookmark. Prefer concrete nouns the page is about (language, framework, " +
                     "topic, domain). Use TitleCase. No commentary, no prose.";

        var cleanItems = items.Select(i => new
        {
            id = i.Id.ToString(),
            title = string.IsNullOrWhiteSpace(i.Title) ? "(untitled)" : i.Title,
            url = string.IsNullOrWhiteSpace(i.Url) ? "(none)" : i.Url
        }).ToList();

        var user = JsonSerializer.Serialize(cleanItems);

        var body = new
        {
            model = opts.Model,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            },
            temperature = opts.Temperature,
            max_tokens = 4000,
            response_format = new { type = "json_object" }
        };

        try
        {
            var http = _httpFactory.CreateClient(nameof(AiTaggingService));
            using var req = new HttpRequestMessage(HttpMethod.Post, opts.Endpoint) { Content = JsonContent.Create(body, options: JsonOptions) };
            if (!string.IsNullOrWhiteSpace(opts.ApiKey))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);

            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            http.Timeout = opts.Timeout;

            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var errorContent = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                throw new HttpRequestException($"AI endpoint returned status {(int)resp.StatusCode} ({resp.ReasonPhrase}). Details: {errorContent}");
            }

            var raw = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseBatchTags(raw);
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "AI batch tagging failed for {Count} items.", items.Count);
            throw;
        }
    }

    private static Dictionary<Guid, List<string>> ParseBatchTags(string rawJson)
    {
        var result = new Dictionary<Guid, List<string>>();
        if (string.IsNullOrWhiteSpace(rawJson))
            return result;

        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("tags", out var tagsEl))
            {
                root = tagsEl;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (Guid.TryParse(prop.Name, out var id) && prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        var tagsList = prop.Value.EnumerateArray()
                            .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .Select(s => s!.Trim())
                            .Take(6)
                            .ToList();
                        result[id] = tagsList;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Ignore parse errors, caller handles fallback
        }
        return result;
    }

    private static List<string> ParseTags(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
            return new List<string>();

        // The chat API already returns a JSON object wrapper. Some local
        // servers ignore response_format and return bare prose, so parse
        // defensively: look for the tags array anywhere in the payload.
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
            {
                return tagsEl.EnumerateArray()
                    .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s!.Trim())
                    .Distinct(System.StringComparer.OrdinalIgnoreCase)
                    .Take(6)
                    .ToList();
            }
        }
        catch (JsonException)
        {
            // fall through to regex salvage
        }

        // Salvage: extract quoted strings anywhere in the payload as tags.
        var salvaged = System.Text.RegularExpressions.Regex.Matches(rawJson, "\"([\\w\\s\\-./+]{2,30})\"")
            .Select(m => m.Groups[1].Value.Trim())
            .Where(s => !s.Equals("tags", System.StringComparison.OrdinalIgnoreCase))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();
        return salvaged;
    }
}

/// <summary>
/// AI tagging configuration bound from the "AiTagging" section of appsettings.
/// </summary>
public sealed class AiTaggingOptions
{
    public bool IsEnabled { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public double Temperature { get; set; } = 0.2;
    public System.TimeSpan Timeout { get; set; } = System.TimeSpan.FromSeconds(120);

    public static AiTaggingOptions Bind(IConfigurationSection section)
    {
        var opts = new AiTaggingOptions();
        if (section is null) return opts;

        opts.IsEnabled = bool.TryParse(section["Enabled"], out var en) && en;
        opts.Endpoint = section["Endpoint"] ?? string.Empty;
        opts.ApiKey = section["ApiKey"] ?? string.Empty;
        opts.Model = section["Model"] ?? string.Empty;
        if (double.TryParse(section["Temperature"], out var t)) opts.Temperature = t;
        if (int.TryParse(section["TimeoutSeconds"], out var secs)) opts.Timeout = System.TimeSpan.FromSeconds(secs);

        // Environment variables win over appsettings, so a secret never has to
        // live in the repo. Conventional names for OpenAI/Ollama.
        var envKey = Environment.GetEnvironmentVariable("AI_TAGGING__API_KEY");
        if (!string.IsNullOrWhiteSpace(envKey)) opts.ApiKey = envKey;
        var envEndpoint = Environment.GetEnvironmentVariable("AI_TAGGING__ENDPOINT");
        if (!string.IsNullOrWhiteSpace(envEndpoint)) opts.Endpoint = envEndpoint;
        var envModel = Environment.GetEnvironmentVariable("AI_TAGGING__MODEL");
        if (!string.IsNullOrWhiteSpace(envModel)) opts.Model = envModel;
        var envEnabled = Environment.GetEnvironmentVariable("AI_TAGGING__ENABLED");
        if (bool.TryParse(envEnabled, out var ee)) opts.IsEnabled = ee;

        return opts;
    }
}
