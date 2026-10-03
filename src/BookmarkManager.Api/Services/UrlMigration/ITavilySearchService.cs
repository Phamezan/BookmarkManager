namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Test seam for <see cref="TavilySearchService"/> so target-host discovery can be exercised with a
/// fake that counts paid calls without contacting the real API. Production uses the concrete
/// service; the interface exposes the same diagnostics-aware entry point.
/// </summary>
public interface ITavilySearchService
{
    Task<SearchOutcome<SearchCandidate>> SearchWithDiagnosticsAsync(
        SeriesExtraction extraction, string deadHost, SearchRunContext run, CancellationToken ct,
        string? preferredHost = null, bool restrictToPreferredHost = false, string? queryOverride = null);
}
