namespace BookmarkManager.Api.Services.UrlMigration;

// NOTE: SeriesExtraction is defined in ISeriesExtractionService.cs and SearchCandidate in
// IAlternativeUrlSearchService.cs (owned by the extraction/search-service agents, plan
// section 6.1/6.2/6.3).

/// <summary>
/// Verifies a single search candidate URL by fetching it and checking that it plausibly
/// hosts the requested series/chapter. See plan section 6.4.
/// </summary>
public interface ICandidateVerificationService
{
    Task<VerificationResult> VerifyAsync(SearchCandidate candidate, SeriesExtraction extraction, CancellationToken ct);

    /// <summary>
    /// Fetches <paramref name="seriesPageUrl"/> and returns every same-host link href found on the
    /// page, resolved to absolute URLs. Used as a fallback when guessed chapter deep-link patterns
    /// (e.g. "/chapter-35") don't match a site's actual URL scheme (e.g. fanfox's "/c035/1.html") —
    /// the caller filters this list for links whose path plausibly names the target chapter.
    /// </summary>
    Task<IReadOnlyList<string>> DiscoverPageLinksAsync(string seriesPageUrl, CancellationToken ct);
}

/// <summary>
/// Result of verifying a single candidate URL.
/// </summary>
/// <param name="Reachable">True when the final response was a 2xx (after following redirects) and was not a challenge page.</param>
/// <param name="SeriesMatched">True when the page title/og:title normalizes to include most of the series name tokens.</param>
/// <param name="ChapterMatched">True when the chapter number was found in the final URL path or the page title.</param>
/// <param name="Detail">Human-readable note surfaced on the proposal (e.g. "Cloudflare challenge", "404 Not Found").</param>
public sealed record VerificationResult(bool Reachable, bool SeriesMatched, bool ChapterMatched, string Detail);
