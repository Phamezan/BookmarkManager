using System.Net.Http.Json;
using System.Text.Json;

namespace BookmarkManager.Api.Services.Novels;

public sealed class NovelSourceUnavailableException : Exception
{
    public NovelSourceUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

public sealed record SidecarSourceRecord(string Id, string Name, string Site);

public sealed record SidecarNovelSummaryRecord(string Title, string Path, string? Cover);

public sealed record SidecarChapterRecord(string Name, string Path, double? Number, string? ReleaseTime, bool Locked);

public sealed record SidecarNovelDetailRecord(
    string Title,
    string Path,
    string? Cover,
    string? Author,
    string Status,
    List<string> Genres,
    string Summary,
    List<SidecarChapterRecord> Chapters);

public sealed record SidecarChapterHtmlRecord(string Html);

public sealed record SidecarStatsRecord(
    int? Rank,
    double? Rating,
    int? RatingCount,
    long? Views,
    int? Bookmarks,
    int? Chapters,
    string? Status,
    string? LatestChapterName,
    string? LatestChapterUpdated,
    int? ReviewCount,
    double? ReviewAverage,
    string? Summary = null);

public sealed record SidecarReviewParagraphRecord(string Text, bool Spoiler);

public sealed record SidecarReviewItemRecord(
    string Author,
    string? Date,
    double? Score,
    int? Likes,
    List<SidecarReviewParagraphRecord> Paragraphs);

public sealed record SidecarReviewsPageRecord(
    double? Average,
    int? Count,
    int Page,
    bool HasNextPage,
    List<SidecarReviewItemRecord> Items);

public sealed record SidecarRoyalRoadSearchHitRecord(string Title, string Path, string? Cover);

public sealed record SidecarRoyalRoadFictionRecord(
    string? Title,
    double? OverallScore,
    int? Ratings,
    long? Views,
    int? Followers,
    int? Favorites,
    int? Count,
    int Page,
    bool HasNextPage,
    List<SidecarReviewItemRecord> Items);

public interface INovelSidecarClient
{
    Task<IReadOnlyList<SidecarSourceRecord>> GetSourcesAsync(CancellationToken ct);
    Task<IReadOnlyList<SidecarNovelSummaryRecord>> GetPopularAsync(string source, int page, CancellationToken ct);
    Task<IReadOnlyList<SidecarNovelSummaryRecord>> SearchAsync(string source, string query, int page, CancellationToken ct);
    Task<SidecarNovelDetailRecord> GetNovelAsync(string source, string path, CancellationToken ct);
    Task<string> GetChapterHtmlAsync(string source, string path, CancellationToken ct);
    Task<SidecarStatsRecord> GetStatsAsync(string source, string path, CancellationToken ct);
    Task<SidecarReviewsPageRecord> GetReviewsAsync(string source, string path, int page, CancellationToken ct);
    Task<IReadOnlyList<SidecarRoyalRoadSearchHitRecord>> SearchRoyalRoadAsync(string title, CancellationToken ct);
    Task<SidecarRoyalRoadFictionRecord> GetRoyalRoadFictionAsync(string path, int page, CancellationToken ct);
}

public sealed class NovelSidecarClient : INovelSidecarClient
{
    public const string HttpClientName = nameof(NovelSidecarClient);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<NovelSidecarClient> _logger;

    public NovelSidecarClient(HttpClient http, ILogger<NovelSidecarClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public Task<IReadOnlyList<SidecarSourceRecord>> GetSourcesAsync(CancellationToken ct)
        => GetAsync<IReadOnlyList<SidecarSourceRecord>>("sources", ct);

    public Task<IReadOnlyList<SidecarNovelSummaryRecord>> GetPopularAsync(string source, int page, CancellationToken ct)
        => GetAsync<IReadOnlyList<SidecarNovelSummaryRecord>>(
            $"popular?source={Uri.EscapeDataString(source)}&page={page}", ct);

    public Task<IReadOnlyList<SidecarNovelSummaryRecord>> SearchAsync(string source, string query, int page, CancellationToken ct)
        => GetAsync<IReadOnlyList<SidecarNovelSummaryRecord>>(
            $"search?source={Uri.EscapeDataString(source)}&q={Uri.EscapeDataString(query)}&page={page}", ct);

    public Task<SidecarNovelDetailRecord> GetNovelAsync(string source, string path, CancellationToken ct)
        => GetAsync<SidecarNovelDetailRecord>(
            $"novel?source={Uri.EscapeDataString(source)}&path={Uri.EscapeDataString(path)}", ct);

    public async Task<string> GetChapterHtmlAsync(string source, string path, CancellationToken ct)
    {
        var payload = await GetAsync<SidecarChapterHtmlRecord>(
            $"chapter?source={Uri.EscapeDataString(source)}&path={Uri.EscapeDataString(path)}", ct)
            .ConfigureAwait(false);
        return payload.Html ?? string.Empty;
    }

    public Task<SidecarStatsRecord> GetStatsAsync(string source, string path, CancellationToken ct)
        => GetAsync<SidecarStatsRecord>(
            $"stats?source={Uri.EscapeDataString(source)}&path={Uri.EscapeDataString(path)}", ct);

    public Task<SidecarReviewsPageRecord> GetReviewsAsync(string source, string path, int page, CancellationToken ct)
        => GetAsync<SidecarReviewsPageRecord>(
            $"reviews?source={Uri.EscapeDataString(source)}&path={Uri.EscapeDataString(path)}&page={page}", ct);

    public Task<IReadOnlyList<SidecarRoyalRoadSearchHitRecord>> SearchRoyalRoadAsync(string title, CancellationToken ct)
        => GetAsync<IReadOnlyList<SidecarRoyalRoadSearchHitRecord>>(
            $"rr/search?title={Uri.EscapeDataString(title)}", ct);

    public Task<SidecarRoyalRoadFictionRecord> GetRoyalRoadFictionAsync(string path, int page, CancellationToken ct)
        => GetAsync<SidecarRoyalRoadFictionRecord>(
            $"rr/fiction?path={Uri.EscapeDataString(path)}&page={page}", ct);

    private async Task<T> GetAsync<T>(string relativeUrl, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(relativeUrl, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Novel sidecar returned {StatusCode} for {Url}", (int)response.StatusCode, relativeUrl);
                throw new NovelSourceUnavailableException($"Sidecar returned HTTP {(int)response.StatusCode}");
            }

            var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
            if (result is null)
            {
                throw new NovelSourceUnavailableException("Sidecar returned empty JSON payload");
            }

            return result;
        }
        catch (NovelSourceUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Novel sidecar request failed for {Url}", relativeUrl);
            throw new NovelSourceUnavailableException("Novel source unavailable", ex);
        }
    }
}
