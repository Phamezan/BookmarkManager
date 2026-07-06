using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.BookmarkTagging;

public record ProviderTagResult(List<string> Tags, bool WasRejected, string? RejectionReason);

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
    string? ResolvedCoverImageUrl = null);

public interface IAnilistTagProvider
{
    Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken);
}

public interface IAnilistScheduleProvider
{
    Task<List<AnimeMatchCandidateDto>> SearchCandidatesAsync(string title, string? url, CancellationToken cancellationToken);
    Task<AnimeScheduleResult> GetAiringScheduleAsync(int aniListId, CancellationToken cancellationToken);
    Task<AnimeMatchCandidateDto?> FindBestMatchAsync(string title, string? url, CancellationToken cancellationToken);
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

public interface INovelFullTagProvider
{
    Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken);
}

public interface INovelUpdatesTagProvider
{
    Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken);
}
