namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Pre-run sanity check kept separate from <see cref="ICandidateVerificationService"/> so that
/// interface stays focused on single-candidate verification (plan section 6.4, final
/// paragraph). Implemented by <see cref="HttpCandidateVerificationService"/> since it reuses
/// the same bounded-fetch machinery, but callers that only need the liveness guard can depend
/// on this narrower interface.
/// </summary>
public interface IDomainLivenessGuard
{
    /// <summary>
    /// Returns true when the domain still appears alive, i.e. at least 20% of the given URLs
    /// (typically the old URLs of bookmarks matched for migration) still return a 2xx response.
    /// The orchestrator should abort the run with
    /// "Domain appears alive — run Link Checker first or double-check the host." when this is true.
    /// </summary>
    Task<bool> IsDomainAliveAsync(IEnumerable<string> urls, CancellationToken ct);
}
