using System.Text.Json;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services;

public class AiTaggingSettingsService
{
    private const string FileName = "ai-tagging-settings.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly ILogger<AiTaggingSettingsService> _logger;
    private readonly string _settingsPath;

    public AiTaggingSettingsService(ILogger<AiTaggingSettingsService> logger)
    {
        _logger = logger;
        var dataRoot = Directory.Exists("/data")
            ? "/data"
            : Path.Combine(AppContext.BaseDirectory, "data");
        _settingsPath = Path.Combine(dataRoot, FileName);
    }

    protected AiTaggingSettingsService(ILogger<AiTaggingSettingsService> logger, string settingsPath)
    {
        _logger = logger;
        _settingsPath = settingsPath;
    }

    public virtual async Task<AiTaggingSettingsDto> GetAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_settingsPath))
            return DefaultSettings();

        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            var settings = await JsonSerializer.DeserializeAsync<AiTaggingSettingsDto>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return Normalize(settings ?? DefaultSettings());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to read AI tagging settings.");
            return DefaultSettings();
        }
    }

    public virtual async Task<AiTaggingSettingsDto> SaveAsync(AiTaggingSettingsDto settings, CancellationToken cancellationToken)
    {
        var normalized = Normalize(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, normalized, JsonOptions, cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    private static AiTaggingSettingsDto DefaultSettings()
        => new()
        {
            Enabled = false,
            Endpoint = "https://generativelanguage.googleapis.com/v1beta",
            BaseUrl = "https://openrouter.ai/api/v1",
            Model = "google/gemini-2.5-flash:free",
            ApiKey = string.Empty,
            RequestsPerMinute = 15,
            PreferredBatchSize = 25,
            GroqApiKey = string.Empty,
            GroqModel = "llama-3.3-70b-versatile",
            GroqBaseUrl = "https://api.groq.com/openai/v1",
            GroqRequestsPerMinute = 25,
            RagModel = "llama-3.3-70b-versatile",
            RagApiKey = string.Empty,
            RagBaseUrl = "https://api.groq.com/openai/v1",
            RagRequestsPerMinute = 15
        };

    private static AiTaggingSettingsDto Normalize(AiTaggingSettingsDto settings)
        => new()
        {
            Enabled = settings.Enabled,
            Endpoint = string.IsNullOrWhiteSpace(settings.Endpoint)
                ? "https://generativelanguage.googleapis.com/v1beta"
                : settings.Endpoint.Trim().TrimEnd('/'),
            BaseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl)
                ? "https://openrouter.ai/api/v1"
                : settings.BaseUrl.Trim().TrimEnd('/'),
            Model = string.IsNullOrWhiteSpace(settings.Model) ? "google/gemini-2.5-flash:free" : settings.Model.Trim(),
            ApiKey = settings.ApiKey?.Trim() ?? string.Empty,
            RequestsPerMinute = settings.RequestsPerMinute <= 0 ? 15 : settings.RequestsPerMinute,
            PreferredBatchSize = settings.PreferredBatchSize <= 0 ? 25 : settings.PreferredBatchSize,
            GroqApiKey = settings.GroqApiKey?.Trim() ?? string.Empty,
            GroqModel = string.IsNullOrWhiteSpace(settings.GroqModel) ? "llama-3.3-70b-versatile" : settings.GroqModel.Trim(),
            GroqBaseUrl = string.IsNullOrWhiteSpace(settings.GroqBaseUrl)
                ? "https://api.groq.com/openai/v1"
                : settings.GroqBaseUrl.Trim().TrimEnd('/'),
            GroqRequestsPerMinute = settings.GroqRequestsPerMinute <= 0 ? 25 : settings.GroqRequestsPerMinute,
            MigrationSearchModel = string.IsNullOrWhiteSpace(settings.MigrationSearchModel)
                ? "groq/compound-mini"
                : settings.MigrationSearchModel.Trim(),
            MigrationAutoApproveHigh = settings.MigrationAutoApproveHigh,
            RagModel = string.IsNullOrWhiteSpace(settings.RagModel) ? "llama-3.3-70b-versatile" : settings.RagModel.Trim(),
            RagApiKey = settings.RagApiKey?.Trim() ?? string.Empty,
            RagBaseUrl = string.IsNullOrWhiteSpace(settings.RagBaseUrl)
                ? "https://api.groq.com/openai/v1"
                : settings.RagBaseUrl.Trim().TrimEnd('/'),
            RagRequestsPerMinute = settings.RagRequestsPerMinute <= 0 ? 15 : settings.RagRequestsPerMinute
        };
}
