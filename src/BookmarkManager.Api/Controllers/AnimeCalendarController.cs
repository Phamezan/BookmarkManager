using System.Text.Json;
using AutoMapper;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Controllers;

[ApiController]
[Route("api/anime-calendar")]
public class AnimeCalendarController : ControllerBase
{
    // A bookmark that failed to match (or that we haven't checked yet) is only re-attempted
    // after this long - otherwise every auto-match click would re-spend AI + provider requests
    // on the same handful of titles that will never resolve.
    private static readonly TimeSpan MatchAttemptCooldown = TimeSpan.FromDays(7);

    // How long a "no upcoming episodes" verdict stands before the series is queried again. Long
    // enough to keep the vast majority of finished series off AniList's rate-limited API on every
    // load, short enough that a newly announced sequel season shows up within a few days.
    private static readonly TimeSpan NoUpcomingRecheckInterval = TimeSpan.FromDays(3);

    // Max AniList schedule lookups per calendar load. Keeps a cold library (100+ unchecked series)
    // from stampeding AniList's rate limit; the persistent ScheduleCheckedAt cache means the backlog
    // drains over a handful of loads and steady-state loads only re-check the few airing series.
    private const int MaxScheduleQueriesPerLoad = 20;

    // AniList schedules are cached in the DB for this long, so a running server (or a
    // restart) serves them without re-querying. Airing times rarely move within a day
    // for a personal calendar, so a full day balances freshness against API pressure.
    private static readonly TimeSpan ScheduleCacheDuration = TimeSpan.FromDays(1);

    private readonly AppDbContext _db;
    private readonly IMapper _mapper;
    private readonly IAnilistScheduleProvider _anilistSchedule;
    private readonly AiSeriesIdentifierService _aiSeriesIdentifier;

    public AnimeCalendarController(
        AppDbContext db,
        IMapper mapper,
        IAnilistScheduleProvider anilistSchedule,
        AiSeriesIdentifierService aiSeriesIdentifier)
    {
        _db = db;
        _mapper = mapper;
        _anilistSchedule = anilistSchedule;
        _aiSeriesIdentifier = aiSeriesIdentifier;
    }

    [HttpGet("candidates/{bookmarkId:guid}")]
    public async Task<ActionResult<List<AnimeMatchCandidateDto>>> GetCandidatesAsync(Guid bookmarkId, CancellationToken ct)
    {
        var bookmark = await _db.BookmarkNodes
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.Id == bookmarkId && !n.IsDeleted, ct);
        if (bookmark is null) return NotFound();

        var aiTitles = await TryGetAiCanonicalTitlesAsync([bookmark], ct);
        var searchTitle = aiTitles.GetValueOrDefault(bookmark.Id, bookmark.Title);

        var candidates = await SearchCandidatesWithFallbackAsync(searchTitle, bookmark.Url, ct);
        return Ok(candidates);
    }

    [HttpPost("auto-match")]
    public async Task<ActionResult<AutoMatchAnimeResponse>> AutoMatchAsync(AutoMatchAnimeRequest request, CancellationToken ct)
    {
        var unmatched = (await GetAnimeBookmarksInScopeAsync(request.FolderIds, ct))
            .Where(b => !b.AniListId.HasValue)
            .ToList();

        // A sync event only auto-matches bookmarks newly unmatched since the client's last load,
        // instead of re-attempting the whole backlog - keeps a stray unrelated bookmark edit from
        // triggering a full sequential re-match of every still-unmatched bookmark in scope.
        if (request.BookmarkIds is { Count: > 0 } restrictedIds)
        {
            var idSet = new HashSet<Guid>(restrictedIds);
            unmatched = unmatched.Where(b => idSet.Contains(b.Id)).ToList();
        }

        var cutoff = DateTime.UtcNow - MatchAttemptCooldown;
        var needingAttempt = unmatched.Where(b => b.LastMatchAttemptAt is null || b.LastMatchAttemptAt < cutoff).ToList();

        var response = new AutoMatchAnimeResponse
        {
            SkippedCooldownCount = unmatched.Count - needingAttempt.Count
        };

        var aiTitles = await TryGetAiCanonicalTitlesAsync(needingAttempt, ct);

        // One batched AniList call resolves every bookmark's best match at once (aliased search
        // queries) instead of one request per bookmark - see FindBestMatchesBatchAsync.
        var matchItems = needingAttempt
            .Select(b => (b.Id, Title: aiTitles.GetValueOrDefault(b.Id, b.Title), b.Url))
            .ToList();
        var bestMatches = await FindBestMatchesBatchWithFallbackAsync(matchItems, response, ct);

        foreach (var bookmark in needingAttempt)
        {
            var searchTitle = aiTitles.GetValueOrDefault(bookmark.Id, bookmark.Title);
            var lookup = bestMatches.GetValueOrDefault(bookmark.Id);

            // AniList outage/rate-limit (e.g. a 429 mid-run): leave this bookmark untouched -
            // crucially without stamping LastMatchAttemptAt - so it's retried on the next run
            // instead of being locked out by the 7-day cooldown for a failure that was never
            // about the title.
            if (lookup is null || lookup.Unavailable)
                continue;

            bookmark.LastMatchAttemptAt = DateTime.UtcNow;

            var best = lookup.Match;
            if (best is null)
            {
                response.Skipped.Add(new AutoMatchAnimeEntryDto { BookmarkId = bookmark.Id, Title = bookmark.Title, SearchTitle = searchTitle, SkipReason = "No confident match found" });
                continue;
            }

            ApplyMatch(bookmark, best);
            response.Matched.Add(new AutoMatchAnimeEntryDto
            {
                BookmarkId = bookmark.Id,
                Title = bookmark.Title,
                SearchTitle = searchTitle,
                Source = best.Source,
                AniListId = best.AniListId,
                MatchedTitle = best.RomajiTitle,
                Status = best.Status
            });
        }

        await _db.SaveChangesAsync(ct);
        return Ok(response);
    }

    [HttpPost("match")]
    public async Task<ActionResult<BookmarkNodeDto>> ConfirmMatchAsync(ConfirmAnimeMatchRequest request, CancellationToken ct)
    {
        var bookmark = await _db.BookmarkNodes
            .FirstOrDefaultAsync(n => n.Id == request.BookmarkId && !n.IsDeleted, ct);
        if (bookmark is null) return NotFound();

        bookmark.AniListId = request.AniListId;
        bookmark.MediaStatus = request.Status;
        bookmark.AniListMatchedAt = DateTime.UtcNow;
        bookmark.LastMatchAttemptAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(_mapper.Map<BookmarkNodeDto>(bookmark));
    }

    [HttpDelete("match/{bookmarkId:guid}")]
    public async Task<ActionResult<BookmarkNodeDto>> ClearMatchAsync(Guid bookmarkId, CancellationToken ct)
    {
        var bookmark = await _db.BookmarkNodes
            .FirstOrDefaultAsync(n => n.Id == bookmarkId && !n.IsDeleted, ct);
        if (bookmark is null) return NotFound();

        bookmark.AniListId = null;
        bookmark.AniListMatchedAt = null;
        bookmark.MediaStatus = null;
        bookmark.LastMatchAttemptAt = null;
        await _db.SaveChangesAsync(ct);

        return Ok(_mapper.Map<BookmarkNodeDto>(bookmark));
    }

    [HttpGet("schedule")]
    public async Task<ActionResult<AnimeCalendarScheduleResponse>> GetScheduleAsync(
        [FromQuery] Guid[] folderIds, CancellationToken ct)
    {
        var animeBookmarks = await GetAnimeBookmarksInScopeAsync(folderIds, ct);

        var matched = animeBookmarks.Where(b => b.AniListId.HasValue).ToList();
        var unmatched = animeBookmarks.Where(b => !b.AniListId.HasValue).ToList();

        var entries = new List<AnimeCalendarEntryDto>();
        var seriesWithEntries = new HashSet<string>();
        var dbChanged = false;
        var now = DateTime.UtcNow;

        // The user bookmarks per-episode (e.g. many "One Piece 1155" rows all pointing at the
        // same series), so multiple bookmarks routinely share one media id. Group by that id
        // and emit a single set of calendar entries per series instead of one duplicate set
        // per bookmark, and hit AniList only once per series.
        var recheckCutoff = now - NoUpcomingRecheckInterval;

        // Only consider series that are due for a check - never confirmed empty (ScheduleCheckedAt
        // null, i.e. airing or brand-new) or whose verdict has expired. Prioritise the null ones
        // (airing series must always refresh) then the stalest.
        var candidateGroups = matched
            .GroupBy(GetMediaKey)
            .Where(g => g.Any(b => b.ScheduleCheckedAt is null || b.ScheduleCheckedAt <= recheckCutoff))
            .OrderBy(g => g.Min(b => b.ScheduleCheckedAt ?? DateTime.MinValue))
            .ToList();

        // Persistent schedule cache: a fresh row serves a series without touching AniList, so a
        // long-running server (and every restart) skips the network for anything cached in the
        // last day. Only series with no fresh row get fetched, and only those count against the
        // per-load AniList cap - a warm library loads instantly regardless of size.
        var candidateIds = candidateGroups.Select(g => g.First().AniListId!.Value).ToList();
        var cacheRows = await _db.AnimeScheduleCaches
            .Where(c => candidateIds.Contains(c.AniListId))
            .ToDictionaryAsync(c => c.AniListId, ct);

        var results = new Dictionary<int, AnimeScheduleResult>();
        var neverCheckedFetch = new List<int>();
        var recheckFetch = new List<int>();
        foreach (var group in candidateGroups)
        {
            var id = group.First().AniListId!.Value;
            if (cacheRows.TryGetValue(id, out var row) && row.ExpiresAtUtc > now && !string.IsNullOrEmpty(row.ResolvedTitle))
            {
                results[id] = FromCache(row);
                continue;
            }

            // Brand-new/never-checked (ScheduleCheckedAt null) always gets fetched this load, however
            // large the recheck backlog is - a just-matched series must never wait behind stale
            // "recheck expired" entries. Only the recheck backlog is subject to the per-load cap.
            if (group.Any(b => b.ScheduleCheckedAt is null))
                neverCheckedFetch.Add(id);
            else
                recheckFetch.Add(id);
        }

        // AniList rate-limits aggressively (429s); cap the recheck-backlog network calls per load so a
        // cold library (100+ stale series) converges over a few loads instead of stampeding the API.
        // The fetches run concurrently (bounded), gated by the provider's own token-bucket limiter.
        var remainingBudget = Math.Max(0, MaxScheduleQueriesPerLoad - neverCheckedFetch.Count);
        var toFetch = neverCheckedFetch.Concat(recheckFetch.Take(remainingBudget)).ToList();
        foreach (var (id, result) in await FetchSchedulesAsync(toFetch, ct))
        {
            results[id] = result;

            // A null status means the fetch itself failed (rate-limit/outage), not a real "nothing
            // airing" answer - don't cache it, so it retries next load.
            if (!string.IsNullOrEmpty(result.Status))
            {
                UpsertScheduleCache(cacheRows, id, result, now);
                dbChanged = true;
            }
        }

        foreach (var group in candidateGroups)
        {
            var representative = group.First();
            if (!results.TryGetValue(representative.AniListId!.Value, out var result))
                continue; // over the per-load fetch cap this time; picked up on a later load

            // A cached row and a fresh successful fetch are both real answers; only a failed fetch
            // (never stored) has a null status.
            var fetchSucceeded = !string.IsNullOrEmpty(result.Status);

            // Backfill status for bookmarks matched before MediaStatus tracking existed (or
            // whose status was unknown at match time). Skip this when the schedule was resolved
            // off a SEQUEL - result.Status then describes the newer season, not the bookmark's
            // own matched media, and must not overwrite it.
            if (fetchSucceeded && result.ResolvedAniListId is null)
            {
                foreach (var bookmark in group)
                {
                    if (!string.Equals(bookmark.MediaStatus, result.Status, StringComparison.Ordinal))
                    {
                        bookmark.MediaStatus = result.Status;
                        dbChanged = true;
                    }
                }
            }

            if (fetchSucceeded)
            {
                // Stamp (or clear) the no-airing verdict for the whole group. Empty = confirmed no
                // upcoming, skip for a while; has episodes = airing, keep it always-queried.
                var stamp = result.Episodes.Count == 0 ? now : (DateTime?)null;
                foreach (var bookmark in group)
                {
                    if (bookmark.ScheduleCheckedAt != stamp)
                    {
                        bookmark.ScheduleCheckedAt = stamp;
                        dbChanged = true;
                    }
                }
            }

            // When schedule resolution followed a SEQUEL, the episodes belong to a newer season -
            // display its title/cover/id so the entry reads as the new season rather than the old
            // bookmark. The click-through still targets the original bookmark (BookmarkId/Url).
            var displayTitle = result.ResolvedTitle ?? representative.Title;
            var displayCover = result.ResolvedCoverImageUrl ?? representative.CoverImageUrl;
            var displayAniListId = result.ResolvedAniListId ?? representative.AniListId;

            foreach (var episode in result.Episodes)
            {
                seriesWithEntries.Add(group.Key);

                entries.Add(new AnimeCalendarEntryDto
                {
                    BookmarkId = representative.Id,
                    Title = displayTitle,
                    Url = representative.Url,
                    Source = "AniList",
                    AniListId = displayAniListId,
                    CoverImageUrl = displayCover,
                    EpisodeNumber = episode.EpisodeNumber,
                    AiringAtUtc = episode.AiringAtUtc,
                    TotalEpisodes = result.TotalEpisodes
                });
            }
        }

        if (dbChanged)
        {
            await _db.SaveChangesAsync(ct);
        }

        // "Finished" for display = matched series (by media id) that produced no upcoming episodes,
        // whether genuinely ended or awaiting an unscheduled next season. Counted per series so the
        // per-episode bookmark pattern doesn't inflate it.
        var totalSeries = matched.Select(GetMediaKey).Distinct().Count();

        return Ok(new AnimeCalendarScheduleResponse
        {
            Entries = entries,
            UnmatchedBookmarks = _mapper.Map<List<BookmarkNodeDto>>(unmatched),
            AiringCount = seriesWithEntries.Count,
            FinishedCount = totalSeries - seriesWithEntries.Count,
            AniListDegraded = _anilistSchedule.IsAniListDegraded
        });
    }

    private static string GetMediaKey(BookmarkNode bookmark)
        => $"a:{bookmark.AniListId!.Value}";

    // Resolves every requested series' schedule (including each one's own SEQUEL walk) via
    // AniList's id_in batch query, so a whole backlog costs a handful of requests instead of
    // one-per-series - see AnilistTaggingService.GetAiringSchedulesBatchAsync.
    private async Task<IReadOnlyList<(int Id, AnimeScheduleResult Result)>> FetchSchedulesAsync(
        IReadOnlyList<int> aniListIds, CancellationToken ct)
    {
        if (aniListIds.Count == 0) return [];

        var results = await _anilistSchedule.GetAiringSchedulesBatchAsync(aniListIds, ct);
        return aniListIds
            .Where(results.ContainsKey)
            .Select(id => (id, results[id]))
            .ToList();
    }

    private static AnimeScheduleResult FromCache(AnimeScheduleCache row)
    {
        var episodes = JsonSerializer.Deserialize<List<AnimeScheduleEpisode>>(row.EpisodesJson) ?? [];
        return new AnimeScheduleResult(row.Status, episodes, row.ResolvedAniListId, row.ResolvedTitle, row.ResolvedCoverImageUrl, row.TotalEpisodes);
    }

    private void UpsertScheduleCache(
        Dictionary<int, AnimeScheduleCache> rows, int aniListId, AnimeScheduleResult result, DateTime now)
    {
        if (!rows.TryGetValue(aniListId, out var row))
        {
            row = new AnimeScheduleCache { AniListId = aniListId };
            _db.AnimeScheduleCaches.Add(row);
            rows[aniListId] = row;
        }

        row.Status = result.Status;
        row.ResolvedAniListId = result.ResolvedAniListId;
        row.ResolvedTitle = result.ResolvedTitle;
        row.ResolvedCoverImageUrl = result.ResolvedCoverImageUrl;
        row.EpisodesJson = JsonSerializer.Serialize(result.Episodes);
        row.TotalEpisodes = result.TotalEpisodes;
        row.ExpiresAtUtc = now.Add(ScheduleCacheDuration);
        row.UpdatedAtUtc = now;
    }

    // Regex-based cleanup (StripStreamingSiteJunk + MediaTitleNormalizer) can't handle every
    // streaming-site title shape. The AI series identifier already solves this problem for
    // auto-tagging, so reuse it here as a pre-pass: it turns noisy titles into the canonical
    // series name AniList actually recognizes, batched in one request per call site.
    private async Task<Dictionary<Guid, string>> TryGetAiCanonicalTitlesAsync(IReadOnlyList<BookmarkNode> bookmarks, CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        if (bookmarks.Count == 0) return result;

        try
        {
            var candidates = bookmarks.Select(b => new AiSeriesIdentifyCandidate(b.Id, b.Title, b.Url, null));
            var summary = await _aiSeriesIdentifier.IdentifyAsync(candidates, ct);
            foreach (var item in summary.Items)
            {
                if (item.Confidence >= 0.5)
                    result[item.Id] = item.CanonicalTitle;
            }
        }
        catch (Exception)
        {
            // AI identification is an optional accuracy boost - disabled/missing key/provider
            // errors just mean falling back to the raw bookmark title for matching.
        }

        return result;
    }

    private async Task<List<AnimeMatchCandidateDto>> SearchCandidatesWithFallbackAsync(string title, string? url, CancellationToken ct)
    {
        try
        {
            return await _anilistSchedule.SearchCandidatesAsync(title, url, ct);
        }
        catch (AniListUnavailableException)
        {
            // AniList is the only anime source now - a global outage just means no candidates
            // this pass; the caller surfaces "no confident match" and the cooldown lets it retry.
            return [];
        }
    }

    private async Task<Dictionary<Guid, BestMatchLookupResult>> FindBestMatchesBatchWithFallbackAsync(
        List<(Guid Id, string Title, string? Url)> items, AutoMatchAnimeResponse response, CancellationToken ct)
    {
        if (items.Count == 0) return new Dictionary<Guid, BestMatchLookupResult>();

        var results = await _anilistSchedule.FindBestMatchesBatchAsync(items, ct);
        if (results.Values.Any(r => r.Unavailable))
            response.AniListUnavailable = true;

        return results;
    }

    private static void ApplyMatch(BookmarkNode bookmark, AnimeMatchCandidateDto best)
    {
        bookmark.AniListId = best.AniListId;
        bookmark.AniListMatchedAt = DateTime.UtcNow;
        bookmark.MediaStatus = best.Status;
    }

    private async Task<List<BookmarkNode>> GetAnimeBookmarksInScopeAsync(IReadOnlyCollection<Guid> folderIds, CancellationToken ct)
    {
        if (folderIds.Count == 0) return [];

        var allFolderIds = new HashSet<Guid>(folderIds);
        foreach (var folderId in folderIds)
        {
            var descendants = await FolderHierarchy.GetDescendantFolderIdsAsync(_db, folderId, ct);
            allFolderIds.UnionWith(descendants);
        }

        var candidateBookmarks = await _db.BookmarkNodes
            .Where(n => n.Type == NodeType.Bookmark
                && !n.IsDeleted
                && n.ParentId != null
                && allFolderIds.Contains(n.ParentId.Value))
            .ToListAsync(ct);

        // Auto-tagging writes the "Anime" domain marker into the Tags list, not the Category
        // field - Category is only ever set when a user manually edits it. Check both so
        // manually-categorized bookmarks are still picked up.
        return candidateBookmarks.Where(IsAnimeBookmark).ToList();
    }

    private static bool IsAnimeBookmark(BookmarkNode node)
    {
        if (string.Equals(node.Category, "Anime", StringComparison.OrdinalIgnoreCase))
            return true;

        return (node.Tags ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Any(tag => string.Equals(tag.Trim(), "Anime", StringComparison.OrdinalIgnoreCase));
    }
}
