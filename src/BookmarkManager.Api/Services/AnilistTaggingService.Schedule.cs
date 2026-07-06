using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services;

public sealed partial class AnilistTaggingService : IAnilistScheduleProvider
{
    private static readonly TimeSpan CandidateCacheDuration = TimeSpan.FromHours(12);
    private static readonly TimeSpan ScheduleCacheDuration = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, CandidateCacheEntry> _candidateCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, ScheduleCacheEntry> _scheduleCache = new();

    // Resolve the best AniList search query for a bookmark. Streaming-site URLs carry the
    // canonical series title in their slug, which beats the boilerplate-heavy page title -
    // prefer it, and flag it as trusted so matching can apply a looser similarity threshold
    // (site slugs often use different word/number forms than AniList's romaji, e.g.
    // "eighty-six-2nd-season" vs "86: Eighty Six Part 2").
    private static (string Query, bool FromSlug) ResolveSearchQuery(string title, string? url)
    {
        var slug = MediaTitleNormalizer.TryTitleFromStreamingUrl(url);
        if (!string.IsNullOrWhiteSpace(slug))
            return (slug, true);

        // Streaming-site page titles are often one run-on phrase with no delimiter separating
        // the real title from junk ("... English Sub/Dub online Free on Aniwatch.to") - strip
        // that before handing off to the segment-based normalizer, which relies on delimiters
        // or a distinct trailing brand segment to find noise.
        var preCleaned = StripStreamingSiteJunk(title, url);
        var normalized = MediaTitleNormalizer.Normalize(preCleaned, url, BookmarkTagDomain.Anime);
        var candidate = normalized.Candidates.FirstOrDefault()?.Query ?? preCleaned;
        return (MediaTitleNormalizer.BuildLooseQuery(candidate), false);
    }

    public Task<List<AnimeMatchCandidateDto>> SearchCandidatesAsync(string title, string? url, CancellationToken cancellationToken)
        => SearchByQueryAsync(ResolveSearchQuery(title, url).Query, cancellationToken);

    private async Task<List<AnimeMatchCandidateDto>> SearchByQueryAsync(string cleanQuery, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cleanQuery) || cleanQuery.Length < 2)
            return [];

        var now = DateTimeOffset.UtcNow;
        if (_candidateCache.TryGetValue(cleanQuery, out var cached) && cached.ExpiresAt > now)
            return cached.Candidates;

        try
        {
            await RateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            var body = new
            {
                query = CandidateSearchQuery,
                variables = new { search = cleanQuery }
            };

            using var resp = await PostAniListAsync(body, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var errorBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("AniList candidate search returned non-success code: {Status}. Body: {Body}", resp.StatusCode, errorBody);
                throw new AniListUnavailableException($"AniList responded with {(int)resp.StatusCode} {resp.StatusCode}.");
            }

            using var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (doc is null) return [];

            var candidates = ParseCandidates(doc.RootElement);
            _candidateCache[cleanQuery] = new CandidateCacheEntry(candidates, now.Add(CandidateCacheDuration));
            return candidates;
        }
        catch (AniListUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query AniList candidates for '{Query}'", cleanQuery);
            return [];
        }
    }

    // Slug-derived queries come straight from the streaming site's own canonical path, so the
    // top AniList result is almost always correct even when token-similarity is dragged down
    // by numeral/word differences - trust them with a looser bar than free-text page titles.
    private const double SlugSimilarityThreshold = 0.34;

    public async Task<AnimeMatchCandidateDto?> FindBestMatchAsync(string title, string? url, CancellationToken cancellationToken)
    {
        var (query, fromSlug) = ResolveSearchQuery(title, url);

        var candidates = await SearchByQueryAsync(query, cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0) return null;

        AnimeMatchCandidateDto? best = null;
        var bestScore = 0.0;
        foreach (var candidate in candidates)
        {
            var candidateTitles = new List<string> { candidate.RomajiTitle };
            if (!string.IsNullOrEmpty(candidate.EnglishTitle))
                candidateTitles.Add(candidate.EnglishTitle);

            var score = MediaTitleNormalizer.ScoreTitleSimilarity(query, candidateTitles);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        var threshold = fromSlug ? SlugSimilarityThreshold : MediaTitleNormalizer.DefaultSimilarityThreshold;
        return bestScore >= threshold ? best : null;
    }

    // How many SEQUEL hops to follow before giving up. A bookmarked finished season can sit
    // several seasons behind the currently-airing one (e.g. a franchise on its 4th cour); a small
    // cap keeps the walk bounded while still reaching any realistic "next season" target.
    private const int MaxSequelHops = 5;

    public async Task<AnimeScheduleResult> GetAiringScheduleAsync(int aniListId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        if (_scheduleCache.TryGetValue(aniListId, out var cached) && cached.ExpiresAt > now)
            return cached.Result;

        var (result, succeeded) = await ResolveScheduleFollowingSequelsAsync(aniListId, cancellationToken).ConfigureAwait(false);

        // Only cache a resolution we actually completed. A failed or cancelled fetch (rate-limit
        // timeout, transient network error, request abort) yields an empty list that is NOT a real
        // "nothing airing" answer - caching it would wrongly hide the series until the cache expires.
        if (succeeded)
            _scheduleCache[aniListId] = new ScheduleCacheEntry(result, now.Add(ScheduleCacheDuration));

        return result;
    }

    // A bookmark matched to a finished season should still surface its franchise's next season.
    // AniList models each season as a separate media linked by a SEQUEL relation, so when the
    // matched media has no upcoming episodes we walk the sequel chain to the newest season and
    // return that season's schedule instead - relabeled via the Resolved* fields so the calendar
    // shows the new season, not the old bookmark title. The matched media is looked up under its
    // own id (the walk only advances while the current season has nothing left to air), so a
    // still-airing series like One Piece returns immediately without ever touching relations.
    // Returns the resolved schedule plus whether resolution actually completed. Success is false
    // when any fetch in the walk failed/was cancelled, so the caller can avoid caching a bogus
    // empty result.
    private async Task<(AnimeScheduleResult Result, bool Succeeded)> ResolveScheduleFollowingSequelsAsync(int aniListId, CancellationToken cancellationToken)
    {
        var visited = new HashSet<int>();
        var currentId = aniListId;

        for (var hop = 0; hop <= MaxSequelHops; hop++)
        {
            if (!visited.Add(currentId))
                break; // Defensive: AniList relation cycles are not expected, but guard anyway.

            var media = await FetchMediaScheduleAsync(currentId, cancellationToken).ConfigureAwait(false);
            if (media is null)
                return (new AnimeScheduleResult(null, []), false);

            var followed = currentId != aniListId;

            // Found the season that has episodes still to air - this is the one to display.
            if (media.Episodes.Count > 0)
            {
                var result = followed
                    ? new AnimeScheduleResult(media.Status, media.Episodes, currentId, media.Title, media.CoverImageUrl)
                    : new AnimeScheduleResult(media.Status, media.Episodes);
                return (result, true);
            }

            // No upcoming episodes here; if a later season exists, continue walking toward it.
            if (media.SequelId is int nextId)
            {
                currentId = nextId;
                continue;
            }

            // End of the chain with nothing upcoming - a real, cacheable "nothing airing" answer.
            var empty = followed
                ? new AnimeScheduleResult(media.Status, media.Episodes, currentId, media.Title, media.CoverImageUrl)
                : new AnimeScheduleResult(media.Status, media.Episodes);
            return (empty, true);
        }

        return (new AnimeScheduleResult(null, []), true);
    }

    private async Task<MediaScheduleNode?> FetchMediaScheduleAsync(int aniListId, CancellationToken cancellationToken)
    {
        try
        {
            await RateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            var body = new
            {
                query = AiringScheduleQuery,
                variables = new { id = aniListId }
            };

            using var resp = await PostAniListAsync(body, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("AniList schedule lookup returned non-success code: {Status}", resp.StatusCode);
                return null;
            }

            using var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
            return doc is null ? null : ParseMediaScheduleNode(doc.RootElement);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to query AniList airing schedule for media {AniListId}", aniListId);
            return null;
        }
    }

    private async Task<HttpResponseMessage> PostAniListAsync(object body, CancellationToken cancellationToken)
    {
        var http = _httpFactory.CreateClient(nameof(AnilistTaggingService));
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BookmarkManager/2.0");
        return await http.PostAsJsonAsync("https://graphql.anilist.co", body, cancellationToken).ConfigureAwait(false);
    }

    private const string CandidateSearchQuery = @"
        query ($search: String) {
          Page(page: 1, perPage: 5) {
            media(search: $search, type: ANIME) {
              id
              title { romaji english }
              coverImage { large }
              status
            }
          }
        }";

    private const string AiringScheduleQuery = @"
        query ($id: Int) {
          Media(id: $id, type: ANIME) {
            id
            status
            title { romaji english }
            coverImage { large }
            airingSchedule(notYetAired: true, perPage: 50) {
              nodes { episode airingAt }
            }
            relations {
              edges {
                relationType
                node { id type status }
              }
            }
          }
        }";

    public static List<AnimeMatchCandidateDto> ParseCandidates(JsonElement root)
    {
        var results = new List<AnimeMatchCandidateDto>();
        if (!root.TryGetProperty("data", out var dataEl) ||
            dataEl.ValueKind != JsonValueKind.Object ||
            !dataEl.TryGetProperty("Page", out var pageEl) ||
            pageEl.ValueKind != JsonValueKind.Object ||
            !pageEl.TryGetProperty("media", out var mediaEl) ||
            mediaEl.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var item in mediaEl.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                continue;

            var romaji = item.TryGetProperty("title", out var titleEl) && titleEl.TryGetProperty("romaji", out var romajiEl) && romajiEl.ValueKind == JsonValueKind.String
                ? romajiEl.GetString() ?? string.Empty
                : string.Empty;
            var english = item.TryGetProperty("title", out var titleEl2) && titleEl2.TryGetProperty("english", out var englishEl) && englishEl.ValueKind == JsonValueKind.String
                ? englishEl.GetString()
                : null;
            var cover = item.TryGetProperty("coverImage", out var coverEl) && coverEl.TryGetProperty("large", out var largeEl) && largeEl.ValueKind == JsonValueKind.String
                ? largeEl.GetString()
                : null;
            var status = item.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                ? statusEl.GetString() ?? string.Empty
                : string.Empty;

            results.Add(new AnimeMatchCandidateDto
            {
                Source = "AniList",
                AniListId = idEl.GetInt32(),
                RomajiTitle = romaji,
                EnglishTitle = english,
                CoverImageUrl = cover,
                Status = status
            });
        }

        return results;
    }

    // Sequel statuses worth following toward: a still-airing or announced-but-unaired next season
    // is exactly what the calendar wants to surface. A finished sequel is still followed (the walk
    // only reaches it when the current season has nothing upcoming), letting the chain skip past
    // intermediate completed seasons to a later one that is airing.
    private static readonly HashSet<string> UpcomingSequelStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "RELEASING", "NOT_YET_RELEASED"
    };

    public static MediaScheduleNode? ParseMediaScheduleNode(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var dataEl) ||
            dataEl.ValueKind != JsonValueKind.Object ||
            !dataEl.TryGetProperty("Media", out var mediaEl) ||
            mediaEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var status = mediaEl.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString()
            : null;

        string? title = null;
        if (mediaEl.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.Object)
        {
            title = (titleEl.TryGetProperty("english", out var enEl) && enEl.ValueKind == JsonValueKind.String ? enEl.GetString() : null)
                ?? (titleEl.TryGetProperty("romaji", out var roEl) && roEl.ValueKind == JsonValueKind.String ? roEl.GetString() : null);
        }

        var cover = mediaEl.TryGetProperty("coverImage", out var coverEl) &&
                    coverEl.ValueKind == JsonValueKind.Object &&
                    coverEl.TryGetProperty("large", out var largeEl) &&
                    largeEl.ValueKind == JsonValueKind.String
            ? largeEl.GetString()
            : null;

        var episodes = new List<AnimeScheduleEpisode>();
        if (mediaEl.TryGetProperty("airingSchedule", out var scheduleEl) &&
            scheduleEl.ValueKind == JsonValueKind.Object &&
            scheduleEl.TryGetProperty("nodes", out var nodesEl) &&
            nodesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in nodesEl.EnumerateArray())
            {
                if (!node.TryGetProperty("episode", out var episodeEl) || episodeEl.ValueKind != JsonValueKind.Number)
                    continue;
                if (!node.TryGetProperty("airingAt", out var airingAtEl) || airingAtEl.ValueKind != JsonValueKind.Number)
                    continue;

                episodes.Add(new AnimeScheduleEpisode(
                    episodeEl.GetInt32(),
                    DateTimeOffset.FromUnixTimeSeconds(airingAtEl.GetInt64())));
            }
        }

        var sequelId = ParseSequelId(mediaEl);
        return new MediaScheduleNode(status, title, cover, episodes, sequelId);
    }

    // Pick the SEQUEL edge to follow. Prefer one that is releasing/not-yet-released (the season the
    // user is waiting on); fall back to any sequel so the walk can hop over a finished middle season
    // toward a later airing one.
    private static int? ParseSequelId(JsonElement mediaEl)
    {
        if (!mediaEl.TryGetProperty("relations", out var relationsEl) ||
            relationsEl.ValueKind != JsonValueKind.Object ||
            !relationsEl.TryGetProperty("edges", out var edgesEl) ||
            edgesEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        int? fallbackSequel = null;
        foreach (var edge in edgesEl.EnumerateArray())
        {
            if (!edge.TryGetProperty("relationType", out var relEl) ||
                relEl.ValueKind != JsonValueKind.String ||
                !string.Equals(relEl.GetString(), "SEQUEL", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!edge.TryGetProperty("node", out var nodeEl) || nodeEl.ValueKind != JsonValueKind.Object)
                continue;
            if (!nodeEl.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String ||
                !string.Equals(typeEl.GetString(), "ANIME", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!nodeEl.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                continue;

            var nodeStatus = nodeEl.TryGetProperty("status", out var nsEl) && nsEl.ValueKind == JsonValueKind.String
                ? nsEl.GetString()
                : null;

            if (nodeStatus is not null && UpcomingSequelStatuses.Contains(nodeStatus))
                return idEl.GetInt32();

            fallbackSequel ??= idEl.GetInt32();
        }

        return fallbackSequel;
    }

    public sealed record MediaScheduleNode(
        string? Status,
        string? Title,
        string? CoverImageUrl,
        List<AnimeScheduleEpisode> Episodes,
        int? SequelId);

    public static string StripStreamingSiteJunk(string title, string? url)
    {
        var cleaned = StreamingJunkRegex().Replace(title, string.Empty).TrimEnd();

        var host = ExtractHostToken(url);
        if (!string.IsNullOrEmpty(host))
        {
            var idx = cleaned.IndexOf(host, StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
                cleaned = cleaned[..idx].TrimEnd(' ', '-', '|', '.', ',');
        }

        return cleaned.Length > 0 ? cleaned : title;
    }

    private static string? ExtractHostToken(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal))
            host = host[4..];
        return host.Split('.', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    [GeneratedRegex(@"(?i)\s+(?:english\s+)?sub(?:bed)?\s*/?\s*dub(?:bed)?\b.*$")]
    private static partial Regex StreamingJunkRegex();

    private sealed record CandidateCacheEntry(List<AnimeMatchCandidateDto> Candidates, DateTimeOffset ExpiresAt);
    private sealed record ScheduleCacheEntry(AnimeScheduleResult Result, DateTimeOffset ExpiresAt);
}
