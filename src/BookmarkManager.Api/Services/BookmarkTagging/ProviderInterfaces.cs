using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public record ProviderTagResult(
    List<string> Tags,
    bool WasRejected,
    string? RejectionReason,
    string? CanonicalTitle = null,
    double? MatchScore = null,
    string? CoverImageUrl = null);

public record AnimeScheduleEpisode(int EpisodeNumber, DateTimeOffset AiringAtUtc);

// Status travels alongside the episode list so callers can backfill BookmarkNode.MediaStatus
// from the same response instead of needing a second request just to learn the show finished.
//
// When schedule resolution follows a SEQUEL chain (a finished season whose franchise has a newer
// season airing), the Resolved* fields describe the media the episodes actually belong to - the
// new season - so the calendar can relabel the entry instead of showing the old bookmark title.
public record AnimeScheduleResult(
    string? Status,
    List<AnimeScheduleEpisode> Episodes,
    int? ResolvedAniListId = null,
    string? ResolvedTitle = null,
    string? ResolvedCoverImageUrl = null,
    int? TotalEpisodes = null);

public interface IAnilistTagProvider
{
    Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken);
}

// Match.Unavailable=true means the underlying lookup failed (outage/rate-limit) - the caller
// should leave the bookmark untouched (not stamp LastMatchAttemptAt, not count as "skipped") so
// it retries next run. Unavailable=false with Match=null means AniList was reachable and simply
// had no confident candidate.
public sealed record BestMatchLookupResult(AnimeMatchCandidateDto? Match, bool Unavailable);

public interface IAnilistScheduleProvider
{
    Task<List<AnimeMatchCandidateDto>> SearchCandidatesAsync(string title, string? url, CancellationToken cancellationToken);
    Task<AnimeScheduleResult> GetAiringScheduleAsync(int aniListId, CancellationToken cancellationToken);
    Task<Dictionary<int, AnimeScheduleResult>> GetAiringSchedulesBatchAsync(IReadOnlyList<int> aniListIds, CancellationToken cancellationToken);

    // Resolves many bookmarks' best AniList match in a handful of requests instead of one search
    // per bookmark - each bookmark has its own title so id_in doesn't apply, GraphQL aliasing does.
    Task<Dictionary<Guid, BestMatchLookupResult>> FindBestMatchesBatchAsync(
        IReadOnlyList<(Guid Id, string Title, string? Url)> items, CancellationToken cancellationToken);

    // True when AniList's own X-RateLimit-Limit response header last reported below its normal 90
    // req/min ceiling (AniList runs a documented "degraded" mode at 30 req/min with no separate
    // status endpoint - this header is the only live signal), so callers can surface it to the user.
    bool IsAniListDegraded { get; }
}

/// <summary>Thrown when AniList's API itself is unreachable or refusing requests (e.g. a global outage), as opposed to a query simply returning zero results.</summary>
public sealed class AniListUnavailableException(string message) : Exception(message);

public interface IMangaUpdatesTagProvider
{
    Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken);
}

public interface IKitsuTagProvider
{
    Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken);
}

public interface ICatalogTagProvider
{
    Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken);
}


