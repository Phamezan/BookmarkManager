using System.Text.Json;

namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Persists the user's permanently-rejected discovery hosts. Kept in its own tiny JSON file
/// (rather than the AI-tagging settings document) so a Settings save never clears the reject
/// list: <see cref="AiTaggingSettingsService.Normalize"/> rebuilds the settings DTO field by
/// field and would silently drop a non-secret field it does not know about. Hosts are stored
/// normalized (lowercase, no leading <c>www.</c>) so "<c>WWW.Comizy.io</c>" and
/// "<c>comizy.io</c>" are the same entry. Discovery excludes these before probing so no HTTP
/// request is ever sent to a host the user rejected.
/// </summary>
public class RejectedTargetHostStore
{
    private const string FileName = "rejected-target-hosts.json";

    /// <summary>Longest valid DNS hostname (RFC 1035 presentation form).</summary>
    public const int MaxHostLength = 253;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly ILogger<RejectedTargetHostStore> _logger;
    private readonly string _storePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RejectedTargetHostStore(ILogger<RejectedTargetHostStore> logger)
    {
        _logger = logger;
        var dataRoot = Directory.Exists("/data")
            ? "/data"
            : Path.Combine(AppContext.BaseDirectory, "data");
        _storePath = Path.Combine(dataRoot, FileName);
    }

    protected RejectedTargetHostStore(ILogger<RejectedTargetHostStore> logger, string storePath)
    {
        _logger = logger;
        _storePath = storePath;
    }

    public virtual async Task<IReadOnlyList<string>> GetHostsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Adds the normalized host. Returns false when it was already present.</summary>
    public virtual async Task<bool> AddAsync(string host, CancellationToken cancellationToken)
    {
        var normalized = Normalize(host);
        if (string.IsNullOrEmpty(normalized))
            return false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var hosts = (await ReadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            if (hosts.Contains(normalized, StringComparer.Ordinal))
                return false;

            hosts.Add(normalized);
            await WriteAsync(hosts, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes the normalized host. Returns false when it was not present.</summary>
    public virtual async Task<bool> RemoveAsync(string host, CancellationToken cancellationToken)
    {
        var normalized = Normalize(host);
        if (string.IsNullOrEmpty(normalized))
            return false;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var hosts = (await ReadAsync(cancellationToken).ConfigureAwait(false)).ToList();
            var removed = hosts.RemoveAll(h => string.Equals(h, normalized, StringComparison.Ordinal));
            if (removed == 0)
                return false;

            await WriteAsync(hosts, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<string>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storePath))
            return [];

        try
        {
            await using var stream = File.OpenRead(_storePath);
            var hosts = await JsonSerializer.DeserializeAsync<List<string>>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            if (hosts is null)
                return [];

            return hosts
                .Select(Normalize)
                .Where(h => !string.IsNullOrEmpty(h))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Failed to read rejected target hosts.");
            return [];
        }
    }

    private async Task WriteAsync(List<string> hosts, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        await using var stream = File.Create(_storePath);
        await JsonSerializer.SerializeAsync(stream, hosts.OrderBy(h => h, StringComparer.Ordinal).ToList(), JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Lowercase, drop a scheme/path if given, and strip a leading <c>www.</c>.</summary>
    public static string Normalize(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return string.Empty;

        var value = host.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            value = absolute.Host;
        }
        // Only bare hosts (no path) get a scheme prefix for parsing; a bare "has/slash.example"
        // must stay invalid rather than being truncated to the "has" host.
        else if (!value.Contains('/') && Uri.TryCreate($"https://{value}", UriKind.Absolute, out var withScheme))
        {
            value = withScheme.Host;
        }

        value = value.Trim().TrimEnd('.').ToLowerInvariant();
        return value.StartsWith("www.", StringComparison.Ordinal) ? value[4..] : value;
    }

    /// <summary>True for a plain, non-private hostname (no scheme, path, whitespace, or IP literal).</summary>
    public static bool IsValidHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > MaxHostLength)
            return false;

        if (host.Any(char.IsWhiteSpace) || host.Contains('/') || host.Contains('?') ||
            host.Contains('\\') || host.Contains('#') || host.Contains('@') || host.Contains(':'))
        {
            return false;
        }

        if (Uri.CheckHostName(host) == UriHostNameType.Unknown)
            return false;

        // Accept only DNS names, never bare IP literals (including private ranges parsed as IPv4).
        return Uri.CheckHostName(host) == UriHostNameType.Dns;
    }
}
