using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace BookmarkManager.Api.Services.UrlMigration;

// NOTE: SeriesExtraction is defined in ISeriesExtractionService.cs (owned by the extraction
// service, plan section 6.1/6.2).
public interface IAlternativeUrlSearchService
{
    /// <param name="preferredHost">
    /// Host that already resolved a series earlier in the same run, if any. Sites that host one
    /// manga/anime series usually host most of the others too, so later bookmarks in a run should
    /// prefer landing back on the same replacement site instead of scattering across many hosts.
    /// </param>
    /// <param name="restrictToPreferredHost">
    /// True when <paramref name="preferredHost"/> is a domain the user explicitly picked as the
    /// migration target (not just auto-learned mid-run) - search is limited to that host only
    /// instead of the open web, so it just confirms the series lives there.
    /// </param>
    Task<IReadOnlyList<SearchCandidate>> SearchAsync(
        SeriesExtraction extraction, string deadHost, CancellationToken ct, string? preferredHost = null, bool restrictToPreferredHost = false);
}

public sealed record SearchCandidate(string Url, string? Title, string? Snippet);
