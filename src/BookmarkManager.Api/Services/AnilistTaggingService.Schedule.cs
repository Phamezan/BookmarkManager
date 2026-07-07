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

    // AniList's query-complexity budget is 500; each aliased search sub-query with this field
    // set costs 9 (empirically confirmed: 55 aliases = 495 succeeds, 56 = 504 fails). Chunking at
    // 40 leaves margin instead of riding the exact edge.
    private const int CandidateBatchChunkSize = 40;

    // Batches many distinct title searches into a handful of requests instead of one per bookmark -
    // each bookmark has its own search string so id_in doesn't apply here, GraphQL aliasing does
    // instead (t0/t1/... each running its own Page(media(search:...))).
    public async Task<Dictionary<string, List<AnimeMatchCandidateDto>>> SearchCandidatesBatchAsync(
        IReadOnlyList<string> queries, CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, List<AnimeMatchCandidateDto>>(StringComparer.OrdinalIgnoreCase);
        var distinct = queries
            .Where(q => !string.IsNullOrWhiteSpace(q) && q.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0) return results;

        var now = DateTimeOffset.UtcNow;
        var pending = new List<string>();
        foreach (var query in distinct)
        {
            if (_candidateCache.TryGetValue(query, out var cached) && cached.ExpiresAt > now)
                results[query] = cached.Candidates;
            else
                pending.Add(query);
        }

        foreach (var chunk in pending.Chunk(CandidateBatchChunkSize))
        {
            await RateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

            var body = new
            {
                query = BuildCandidateBatchQuery(chunk.Length),
                variables = BuildCandidateBatchVariables(chunk)
            };

            using var resp = await PostAniListAsync(body, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var errorBody = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("AniList batch candidate search returned non-success code: {Status}. Body: {Body}", resp.StatusCode, errorBody);
                throw new AniListUnavailableException($"AniList responded with {(int)resp.StatusCode} {resp.StatusCode}.");
            }

            using var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (doc is null) continue;

            var parsed = ParseCandidateBatch(doc.RootElement, chunk);
            foreach (var (query, candidates) in parsed)
            {
                results[query] = candidates;
                _candidateCache[query] = new CandidateCacheEntry(candidates, now.Add(CandidateCacheDuration));
            }
        }

        return results;
    }

    private static string BuildCandidateBatchQuery(int count)
    {
        var varDecls = string.Join(", ", Enumerable.Range(0, count).Select(i => $"$s{i}: String"));
        var aliases = string.Join(" ", Enumerable.Range(0, count).Select(i =>
            $"t{i}: Page(page: 1, perPage: 5) {{ media(search: $s{i}, type: ANIME) {{ id title {{ romaji english }} coverImage {{ large }} status }} }}"));
        return $"query({varDecls}) {{ {aliases} }}";
    }

    private static Dictionary<string, object> BuildCandidateBatchVariables(IReadOnlyList<string> chunk)
    {
        var variables = new Dictionary<string, object>();
        for (var i = 0; i < chunk.Count; i++)
            variables[$"s{i}"] = chunk[i];
        return variables;
    }

    private static Dictionary<string, List<AnimeMatchCandidateDto>> ParseCandidateBatch(JsonElement root, IReadOnlyList<string> chunk)
    {
        var results = new Dictionary<string, List<AnimeMatchCandidateDto>>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object)
            return results;

        for (var i = 0; i < chunk.Count; i++)
        {
            if (dataEl.TryGetProperty($"t{i}", out var pageEl))
                results[chunk[i]] = ParseCandidatesFromPage(pageEl);
        }

        return results;
    }

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

    private static AnimeMatchCandidateDto? ScoreBestCandidate(string query, bool fromSlug, List<AnimeMatchCandidateDto> candidates)
    {
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

    // Resolves every item's search query up front, then runs ONE batched candidate search across
    // all of them (SearchCandidatesBatchAsync dedupes+chunks internally) instead of one AniList
    // request per bookmark. If the batch call fails outright (outage/rate-limit), every item comes
    // back Unavailable=true so the caller retries them all next run instead of burning cooldowns.
    public async Task<Dictionary<Guid, BestMatchLookupResult>> FindBestMatchesBatchAsync(
        IReadOnlyList<(Guid Id, string Title, string? Url)> items, CancellationToken cancellationToken)
    {
        var results = new Dictionary<Guid, BestMatchLookupResult>();
        if (items.Count == 0) return results;

        var resolved = items.ToDictionary(i => i.Id, i => ResolveSearchQuery(i.Title, i.Url));

        Dictionary<string, List<AnimeMatchCandidateDto>> candidatesByQuery;
        try
        {
            candidatesByQuery = await SearchCandidatesBatchAsync(
                resolved.Values.Select(r => r.Query).ToList(), cancellationToken).ConfigureAwait(false);
        }
        catch (AniListUnavailableException)
        {
            foreach (var id in resolved.Keys)
                results[id] = new BestMatchLookupResult(null, Unavailable: true);
            return results;
        }

        foreach (var (id, (query, fromSlug)) in resolved)
        {
            var candidates = candidatesByQuery.GetValueOrDefault(query, []);
            results[id] = new BestMatchLookupResult(ScoreBestCandidate(query, fromSlug, candidates), Unavailable: false);
        }

        return results;
    }

    // How many SEQUEL hops to follow before giving up. A bookmarked finished season can sit
    // several seasons behind the currently-airing one (e.g. a franchise on its 4th cour); a small
    // cap keeps the walk bounded while still reaching any realistic "next season" target.
    private const int MaxSequelHops = 5;

    public async Task<AnimeScheduleResult> GetAiringScheduleAsync(int aniListId, CancellationToken cancellationToken)
    {
        var batch = await GetAiringSchedulesBatchAsync([aniListId], cancellationToken).ConfigureAwait(false);
        return batch.TryGetValue(aniListId, out var result) ? result : new AnimeScheduleResult(null, []);
    }

    // Resolves many series' schedules (including each one's own SEQUEL walk) in a handful of
    // requests total instead of one-per-series-per-hop. AniList's Page(media(id_in:...)) query
    // answers up to 50 ids per call, so every hop level batches all still-unresolved series
    // together: hop 0 fetches every requested id in ~1 call, hop 1 fetches only the ids that came
    // back with zero upcoming episodes and a SEQUEL to follow, and so on. This is the difference
    // between ~90 requests for a 90-bookmark backlog and ~5.
    public async Task<Dictionary<int, AnimeScheduleResult>> GetAiringSchedulesBatchAsync(
        IReadOnlyList<int> aniListIds, CancellationToken cancellationToken)
    {
        var results = new Dictionary<int, AnimeScheduleResult>();
        if (aniListIds.Count == 0) return results;

        var now = DateTimeOffset.UtcNow;
        var pending = new List<int>();
        foreach (var id in aniListIds.Distinct())
        {
            if (_scheduleCache.TryGetValue(id, out var cached) && cached.ExpiresAt > now)
                results[id] = cached.Result;
            else
                pending.Add(id);
        }

        if (pending.Count == 0) return results;

        // originalId -> id currently being looked up for it (advances along the SEQUEL chain).
        var current = pending.ToDictionary(id => id, id => id);
        var visited = pending.ToDictionary(id => id, id => new HashSet<int> { id });
        var resolved = new Dictionary<int, (AnimeScheduleResult Result, bool Succeeded)>();

        for (var hop = 0; hop <= MaxSequelHops && current.Count > 0; hop++)
        {
            var fetched = await FetchMediaBatchAsync(current.Values.Distinct().ToList(), cancellationToken).ConfigureAwait(false);

            var next = new Dictionary<int, int>();
            foreach (var (originalId, currentId) in current)
            {
                if (!fetched.TryGetValue(currentId, out var media))
                {
                    // Fetch failed or media no longer exists - not a real "nothing airing" answer,
                    // don't cache it, so the next load retries.
                    resolved[originalId] = (new AnimeScheduleResult(null, []), false);
                    continue;
                }

                if (media.Episodes.Count > 0)
                {
                    resolved[originalId] = (new AnimeScheduleResult(media.Status, media.Episodes, currentId, media.Title, media.CoverImageUrl), true);
                    continue;
                }

                if (media.SequelId is int nextId && visited[originalId].Add(nextId))
                {
                    next[originalId] = nextId;
                    continue;
                }

                // End of the chain (or a cycle guard tripped) with nothing upcoming - a real,
                // cacheable "nothing airing" answer.
                resolved[originalId] = (new AnimeScheduleResult(media.Status, media.Episodes, currentId, media.Title, media.CoverImageUrl), true);
            }

            current = next;
        }

        // Ran out of hop budget while still walking - a real, cacheable "nothing found" answer,
        // same as the single-series path hitting MaxSequelHops.
        foreach (var originalId in current.Keys)
            resolved[originalId] = (new AnimeScheduleResult(null, []), true);

        foreach (var (originalId, (result, succeeded)) in resolved)
        {
            if (succeeded)
                _scheduleCache[originalId] = new ScheduleCacheEntry(result, now.Add(ScheduleCacheDuration));
            results[originalId] = result;
        }

        return results;
    }

    // Fetches many media by id in one request (AniList's Page(media(id_in:...)) answers up to 50
    // ids per call), chunking only if the caller passes more than that. This is what lets
    // GetAiringSchedulesBatchAsync resolve an entire hop level for every still-pending series
    // with a single round trip instead of one request per id.
    private async Task<Dictionary<int, MediaScheduleNode>> FetchMediaBatchAsync(IReadOnlyList<int> aniListIds, CancellationToken cancellationToken)
    {
        var results = new Dictionary<int, MediaScheduleNode>();
        if (aniListIds.Count == 0) return results;

        foreach (var chunk in aniListIds.Chunk(50))
        {
            try
            {
                await RateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);

                var body = new
                {
                    query = AiringScheduleBatchQuery,
                    variables = new { ids = chunk }
                };

                using var resp = await PostAniListAsync(body, cancellationToken).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogWarning("AniList batch schedule lookup returned non-success code: {Status}", resp.StatusCode);
                    continue;
                }

                using var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
                if (doc is null) continue;

                foreach (var (id, node) in ParseMediaScheduleBatch(doc.RootElement))
                    results[id] = node;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query AniList batch airing schedule for {Count} ids", chunk.Length);
            }
        }

        return results;
    }

    // AniList's own docs note a "degraded" mode capped at 30 req/min (vs the normal 90), with no
    // separate status endpoint to query - the X-RateLimit-Limit response header on every call is
    // the only live signal, so capture it here and surface it to callers instead of hardcoding
    // which mode is currently in effect.
    private static volatile bool _isDegraded;
    public bool IsAniListDegraded => _isDegraded;

    private async Task<HttpResponseMessage> PostAniListAsync(object body, CancellationToken cancellationToken)
    {
        var http = _httpFactory.CreateClient(nameof(AnilistTaggingService));
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BookmarkManager/2.0");
        var resp = await http.PostAsJsonAsync("https://graphql.anilist.co", body, cancellationToken).ConfigureAwait(false);

        if (resp.Headers.TryGetValues("X-RateLimit-Limit", out var limitValues) &&
            int.TryParse(limitValues.FirstOrDefault(), out var limit))
        {
            _isDegraded = limit < 90;
        }

        return resp;
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

    // Batched form of the single-media schedule query: id_in answers up to 50 ids in one call,
    // which is what lets GetAiringSchedulesBatchAsync resolve an entire hop level for every
    // pending series with a single request instead of one request per id.
    private const string AiringScheduleBatchQuery = @"
        query ($ids: [Int]) {
          Page(page: 1, perPage: 50) {
            media(id_in: $ids, type: ANIME) {
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
          }
        }";

    public static List<AnimeMatchCandidateDto> ParseCandidates(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var dataEl) ||
            dataEl.ValueKind != JsonValueKind.Object ||
            !dataEl.TryGetProperty("Page", out var pageEl) ||
            pageEl.ValueKind != JsonValueKind.Object)
        {
            return [];
        }

        return ParseCandidatesFromPage(pageEl);
    }

    private static List<AnimeMatchCandidateDto> ParseCandidatesFromPage(JsonElement pageEl)
    {
        var results = new List<AnimeMatchCandidateDto>();
        if (!pageEl.TryGetProperty("media", out var mediaEl) || mediaEl.ValueKind != JsonValueKind.Array)
            return results;

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

    // Single-media shape (data.Media), kept for the rare direct-lookup case and covered by
    // existing parsing unit tests; the batch path (data.Page.media[]) is what production code uses.
    public static MediaScheduleNode? ParseMediaScheduleNode(JsonElement root)
    {
        if (!root.TryGetProperty("data", out var dataEl) ||
            dataEl.ValueKind != JsonValueKind.Object ||
            !dataEl.TryGetProperty("Media", out var mediaEl) ||
            mediaEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ParseMediaScheduleFields(mediaEl);
    }

    // Parses the Page(media(id_in:...)) batch response into a per-id lookup.
    public static Dictionary<int, MediaScheduleNode> ParseMediaScheduleBatch(JsonElement root)
    {
        var results = new Dictionary<int, MediaScheduleNode>();
        if (!root.TryGetProperty("data", out var dataEl) ||
            dataEl.ValueKind != JsonValueKind.Object ||
            !dataEl.TryGetProperty("Page", out var pageEl) ||
            pageEl.ValueKind != JsonValueKind.Object ||
            !pageEl.TryGetProperty("media", out var mediaArrayEl) ||
            mediaArrayEl.ValueKind != JsonValueKind.Array)
        {
            return results;
        }

        foreach (var mediaEl in mediaArrayEl.EnumerateArray())
        {
            if (!mediaEl.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
                continue;

            results[idEl.GetInt32()] = ParseMediaScheduleFields(mediaEl);
        }

        return results;
    }

    private static MediaScheduleNode ParseMediaScheduleFields(JsonElement mediaEl)
    {
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
