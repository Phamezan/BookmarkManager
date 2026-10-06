namespace BookmarkManager.Contracts;

// Force = true skips the "domain still appears alive" liveness guard. Set when the user
// manually names a host, since that's them asserting it's dead/unusable, not something
// auto-detected from Link Checker that still needs a sanity check.
// SuggestedHost = optional target domain the user already picked as the replacement -
// search is restricted to that host instead of the open web, and every bookmark in the run
// tries it first regardless of what earlier bookmarks resolved to.
// Pattern = optional URL template learned from target-host discovery (e.g. "/{slug}/chapter-{n}").
// When set, the direct target-host rewrite tries that shape before the built-in ones.
public record StartUrlMigrationRequest(string DeadHost, bool Force = false, string? SuggestedHost = null, string? Pattern = null);   // host only, e.g. "flamecomics.xyz"

// Target-host discovery: sample the dead host's bookmarks, find where those series live now
// (SearXNG first, Tavily only as a paid fallback), probe candidates with plain HTTP, and rank
// the hosts by coverage. SampleSize is clamped 5-50 (default 20). CandidateHosts, when supplied,
// skips search entirely (0 credits) and probes only the named hosts (max 5, validated).
public record DiscoverTargetHostRequest(string DeadHost, int? SampleSize = null, List<string>? CandidateHosts = null);

// Host to add to (or remove from) the persisted reject list. Normalized server-side.
public record RejectedTargetHostRequest(string Host);

public class TargetHostSuggestionDto
{
    public string Host { get; set; } = string.Empty;
    public int SeriesFound { get; set; }
    public int ChaptersFound { get; set; }
    public int SampleSize { get; set; }
    public string? BestPattern { get; set; }        // e.g. "/{slug}/chapter-{n}"
    public int SearchCreditsUsed { get; set; }      // Tavily calls that surfaced this host
    public string? Detail { get; set; }
}

public class TargetHostDiscoveryResultDto
{
    public string DeadHost { get; set; } = string.Empty;
    public int SampleSize { get; set; }
    public int SearchCreditsUsed { get; set; }
    public List<TargetHostSuggestionDto> Suggestions { get; set; } = [];
    public string? Detail { get; set; }             // set when nothing qualified
}

public class UrlMigrationStatusDto
{
    public bool IsRunning { get; set; }
    public Guid? RunId { get; set; }
    public string? DeadHost { get; set; }
    public int TotalFound { get; set; }
    public int Processed { get; set; }
    public int Resolved { get; set; }
    public int Unresolved { get; set; }
    public string? TopFailureReason { get; set; }
    public string? CurrentBookmarkTitle { get; set; }
    public string? ErrorMessage { get; set; }
}

public class UrlMigrationProposalDto
{
    public Guid Id { get; set; }
    public Guid BookmarkId { get; set; }
    public string BookmarkTitle { get; set; } = string.Empty;
    public string OldUrl { get; set; } = string.Empty;
    public string? ProposedUrl { get; set; }
    public string? ProposedHost { get; set; }
    public string? SeriesName { get; set; }
    public string? ChapterNumber { get; set; }
    public string Confidence { get; set; } = string.Empty;
    public string? Detail { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    // Suwayomi import (phase 1): set when the proposal points at a Suwayomi manga. Grouped by
    // SourceName in the review list; SeriesName stays the title extracted from the bookmark.
    public bool IsSuwayomi { get; set; }             // created by a Suwayomi import run (also when Unresolved)
    public int? SuwayomiMangaId { get; set; }
    public string? SourceName { get; set; }
    public string? MatchedTitle { get; set; }        // the Suwayomi source's own title
    public string? SourceLatestChapter { get; set; } // highest chapter number on that source
}

public record DecideProposalsRequest(List<Guid> ProposalIds);         // approve or reject
public record SetManualProposalUrlRequest(string Url);                // manual URL entry for an Unresolved proposal
public record UpdateProposalUrlRequest(string Url);                   // edit proposed URL before approval
// Messages carries human-readable per-approval outcomes (e.g. Suwayomi "330 chapters marked read")
// so the client can show them in the approval snackbar. Optional so existing callers are unaffected.
public record DecideProposalsResponse(int Succeeded, int Failed, List<string> Errors, List<string>? Messages = null);

public class DeadDomainCandidateDto      // for the "detected dead domains" panel
{
    public string Host { get; set; } = string.Empty;
    public int BookmarkCount { get; set; }
}
