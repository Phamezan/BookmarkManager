using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.Library;

/// <summary>MangaDex REST provider - manga/manhwa/manhua chapters, no API key required.</summary>
public sealed class MangaDexLibraryProvider(
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    ILogger<MangaDexLibraryProvider> logger)
    : LibraryMediaProviderBase(httpFactory, cache, logger), IMediaProvider, ITrendingMediaProvider, IBulkCatalogProvider
{
    private const string BaseUrl = "https://api.mangadex.org";
    private const int CatalogPageSize = 100;

    /// <summary>MangaDex hard-caps offset+limit at 10,000 - the bounded "manga-popular" ranked slice
    /// stays under that ceiling; full coverage beyond it comes from the unbounded createdAt-cursor
    /// "manga" sequence below, which has no such limit.</summary>
    private const int PopularOffsetCeiling = 9_900;

    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DetailsCacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan ReleaseCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TrendingCacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan CatalogCacheTtl = TimeSpan.FromHours(6);

    private static readonly ProviderRateLimiter RateLimiter = new(
        tokenLimit: 5,
        tokensPerPeriod: 1,
        replenishmentPeriod: TimeSpan.FromSeconds(1));

    public override string ProviderName => "MangaDex";

    public bool IsEnabled => true;

    public Task<IReadOnlyList<LibraryEntryDto>> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken)
    {
        var cleanQuery = query.Trim();
        if (cleanQuery.Length < 2 || mediaType is LibraryMediaType.LightNovel or LibraryMediaType.Webnovel or LibraryMediaType.Anime)
            return Task.FromResult<IReadOnlyList<LibraryEntryDto>>([]);

        var cacheKey = $"{ProviderName}:search:{cleanQuery.ToLowerInvariant()}";
        var url = $"{BaseUrl}/manga?title={Uri.EscapeDataString(cleanQuery)}&limit=12" +
                  "&includes[]=cover_art&includes[]=author" +
                  "&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica" +
                  "&order[relevance]=desc";

        return ExecuteAsync(
            cacheKey,
            SearchCacheTtl,
            TimeSpan.FromSeconds(5),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
                return (IReadOnlyList<LibraryEntryDto>)ParseMangaArray(doc);
            },
            [],
            cancellationToken);
    }

    public Task<IReadOnlyList<LibraryEntryDto>> GetTrendingAsync(LibraryMediaType? mediaType, CancellationToken cancellationToken)
    {
        if (mediaType is LibraryMediaType.LightNovel or LibraryMediaType.Webnovel or LibraryMediaType.Anime)
            return Task.FromResult<IReadOnlyList<LibraryEntryDto>>([]);

        var cacheKey = $"{ProviderName}:trending";
        // MangaDex has no "trending" endpoint - most-followed titles is the closest public proxy.
        var url = $"{BaseUrl}/manga?limit=12&order[followedCount]=desc" +
                  "&includes[]=cover_art&includes[]=author" +
                  "&contentRating[]=safe&contentRating[]=suggestive";

        return ExecuteAsync(
            cacheKey,
            TrendingCacheTtl,
            TimeSpan.FromSeconds(8),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
                return (IReadOnlyList<LibraryEntryDto>)ParseMangaArray(doc);
            },
            [],
            cancellationToken);
    }

    /// <summary>"manga" is an unbounded coverage crawl (createdAt cursor, no popularity signal, bypasses
    /// the 10k offset ceiling); "manga-popular" is a bounded, ranked most-followed slice used for the
    /// default "Trending" ordering. Both feed the same catalog table; upserts are idempotent by
    /// (Provider, ProviderId), so titles found by both simply get their popularity rank filled in.</summary>
    public IReadOnlyList<string> CatalogMediaTypeQueries { get; } = ["manga", "manga-popular"];

    /// <summary>MangaDex's <c>/manga</c> list responses include <c>attributes.description</c>, which
    /// <see cref="MapManga"/> maps to the synopsis - the same field <see cref="GetDetailsAsync"/> would
    /// return, so listing rows are already rich and need no per-title detail fetch.</summary>
    public bool ListingProvidesFullSynopsis => true;

    public Task<CatalogPageResult> GetCatalogPageAsync(string mediaTypeQuery, string? continuationToken, CancellationToken cancellationToken) =>
        mediaTypeQuery == "manga-popular"
            ? GetPopularCatalogPageAsync(continuationToken, cancellationToken)
            : GetExhaustiveCatalogPageAsync(continuationToken, cancellationToken);

    private Task<CatalogPageResult> GetPopularCatalogPageAsync(string? continuationToken, CancellationToken cancellationToken)
    {
        var offset = continuationToken is not null && int.TryParse(continuationToken, out var parsedOffset) ? parsedOffset : 0;
        var cacheKey = $"{ProviderName}:catalog:popular:{offset}";
        var url = $"{BaseUrl}/manga?limit={CatalogPageSize}&offset={offset}&order[followedCount]=desc" +
                  "&includes[]=cover_art&includes[]=author" +
                  "&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica";

        return ExecuteCatalogAsync(
            cacheKey,
            CatalogCacheTtl,
            TimeSpan.FromSeconds(15),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
                if (doc is null)
                    throw new HttpRequestException($"MangaDex popular catalog page at offset {offset} request failed.");
                var results = ParseMangaArray(doc);
                var nextOffset = offset + CatalogPageSize;
                var next = results.Count < CatalogPageSize || nextOffset > PopularOffsetCeiling
                    ? null
                    : nextOffset.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return new CatalogPageResult(results, next, offset);
            },
            cancellationToken);
    }

    private Task<CatalogPageResult> GetExhaustiveCatalogPageAsync(string? continuationToken, CancellationToken cancellationToken)
    {
        var cursor = continuationToken ?? "2000-01-01T00:00:00";
        var cacheKey = $"{ProviderName}:catalog:cursor:{cursor}";
        var url = $"{BaseUrl}/manga?limit={CatalogPageSize}&order[createdAt]=asc&createdAtSince={Uri.EscapeDataString(cursor)}" +
                  "&includes[]=cover_art&includes[]=author" +
                  "&contentRating[]=safe&contentRating[]=suggestive&contentRating[]=erotica";

        return ExecuteCatalogAsync(
            cacheKey,
            CatalogCacheTtl,
            TimeSpan.FromSeconds(15),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
                if (doc is null || !doc.RootElement.TryGetProperty("data", out var dataArray) || dataArray.ValueKind != JsonValueKind.Array)
                    throw new HttpRequestException($"MangaDex exhaustive catalog page at cursor {cursor} request failed.");

                var results = new List<LibraryEntryDto>();
                string? lastCreatedAt = null;
                foreach (var item in dataArray.EnumerateArray())
                {
                    var entry = MapManga(item, ProviderName);
                    if (entry is not null)
                        results.Add(entry);
                    lastCreatedAt = GetCreatedAt(item) ?? lastCreatedAt;
                }

                var next = results.Count < CatalogPageSize ? null : lastCreatedAt;
                return new CatalogPageResult(results, next);
            },
            cancellationToken);
    }

    private List<LibraryEntryDto> ParseMangaArray(JsonDocument? doc)
    {
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var dataArray) || dataArray.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<LibraryEntryDto>();
        foreach (var item in dataArray.EnumerateArray())
        {
            var entry = MapManga(item, ProviderName);
            if (entry is not null)
                results.Add(entry);
        }

        return results;
    }

    private static string? GetCreatedAt(JsonElement item) =>
        item.TryGetProperty("attributes", out var attrs) && attrs.TryGetProperty("createdAt", out var createdAtEl) && createdAtEl.ValueKind == JsonValueKind.String
            ? createdAtEl.GetString()
            : null;

    public Task<LibraryEntryDto?> GetDetailsAsync(string providerId, CancellationToken cancellationToken)
    {
        var cacheKey = $"{ProviderName}:details:{providerId}";
        var url = $"{BaseUrl}/manga/{Uri.EscapeDataString(providerId)}?includes[]=cover_art&includes[]=author";

        return ExecuteAsync(
            cacheKey,
            DetailsCacheTtl,
            TimeSpan.FromSeconds(10),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
                if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    return null;

                return MapManga(data, ProviderName);
            },
            null,
            cancellationToken);
    }

    /// <summary>Global "what's releasing" feed for the Manga calendar - independent of any bookmark.
    /// Covers all origin languages (manga, manhwa, manhua) - no originalLanguage filter, unlike the
    /// earlier manhwa-only cut of this feed. contentRating is capped at safe/suggestive to keep the
    /// feed non-18+ (erotica/pornographic are excluded).</summary>
    public Task<IReadOnlyList<MangaCalendarEntryDto>> GetLatestManhwaReleasesAsync(int limit, CancellationToken cancellationToken)
    {
        var cacheKey = $"{ProviderName}:manga-feed:{limit}";
        var url = $"{BaseUrl}/chapter?limit={limit}&translatedLanguage[]=en" +
                   "&contentRating[]=safe&contentRating[]=suggestive" +
                   "&order[readableAt]=desc&includes[]=manga";

        return ExecuteAsync(
            cacheKey,
            ReleaseCacheTtl,
            TimeSpan.FromSeconds(20),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
                var entries = ParseManhwaFeed(doc);

                // cover_art is a relationship of the manga, not the chapter - /chapter's
                // includes[]=cover_art doesn't attach anything. Batch-fetch covers for the distinct
                // manga in this page via /cover instead.
                var coverUrls = await FetchCoverUrlsAsync(entries.Select(e => e.ProviderId), ct).ConfigureAwait(false);
                foreach (var entry in entries)
                {
                    if (coverUrls.TryGetValue(entry.ProviderId, out var coverUrl))
                        entry.CoverImageUrl = coverUrl;
                }

                return (IReadOnlyList<MangaCalendarEntryDto>)entries;
            },
            [],
            cancellationToken);
    }

    private async Task<Dictionary<string, string>> FetchCoverUrlsAsync(IEnumerable<string> mangaIds, CancellationToken cancellationToken)
    {
        var ids = mangaIds.Distinct().ToList();
        var result = new Dictionary<string, string>();
        if (ids.Count == 0)
            return result;

        var query = string.Join("&", ids.Select(id => $"manga[]={Uri.EscapeDataString(id)}"));
        var url = $"{BaseUrl}/cover?{query}&limit=100";

        await RateLimiter.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var dataArray) || dataArray.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var cover in dataArray.EnumerateArray())
        {
            var mangaId = FindRelationshipId(cover, "manga");
            var fileName = cover.TryGetProperty("attributes", out var coverAttrs) ? GetString(coverAttrs, "fileName") : null;
            if (mangaId is not null && fileName is not null)
                result[mangaId] = $"https://uploads.mangadex.org/covers/{mangaId}/{fileName}.256.jpg";
        }

        return result;
    }

    private List<MangaCalendarEntryDto> ParseManhwaFeed(JsonDocument? doc)
    {
        if (doc is null || !doc.RootElement.TryGetProperty("data", out var dataArray) || dataArray.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<MangaCalendarEntryDto>();
        foreach (var chapter in dataArray.EnumerateArray())
        {
            var entry = MapChapterToReleaseEntry(chapter);
            if (entry is not null)
                results.Add(entry);
        }

        return results;
    }

    private MangaCalendarEntryDto? MapChapterToReleaseEntry(JsonElement chapter)
    {
        if (!chapter.TryGetProperty("attributes", out var attrs))
            return null;

        var mangaId = FindRelationshipId(chapter, "manga");
        if (mangaId is null)
            return null;

        var mangaTitle = FindRelationshipLocalizedTitle(chapter, "manga");
        var title = mangaTitle ?? GetString(attrs, "title") ?? "Untitled";

        DateTimeOffset? readableAt = attrs.TryGetProperty("readableAt", out var readableAtEl) && readableAtEl.ValueKind == JsonValueKind.String &&
                                      DateTimeOffset.TryParse(readableAtEl.GetString(), out var parsed)
            ? parsed
            : null;
        if (readableAt is null)
            return null;

        return new MangaCalendarEntryDto
        {
            Title = title,
            Url = $"https://mangadex.org/title/{mangaId}",
            Provider = ProviderName,
            ProviderId = mangaId,
            CoverImageUrl = null,
            LatestChapter = GetString(attrs, "chapter"),
            LatestVolume = GetString(attrs, "volume"),
            ReleasedAtUtc = readableAt.Value
        };
    }

    private static string? FindRelationshipLocalizedTitle(JsonElement item, string relationshipType)
    {
        if (!item.TryGetProperty("relationships", out var relationships) || relationships.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var rel in relationships.EnumerateArray())
        {
            if (GetString(rel, "type") != relationshipType)
                continue;
            if (rel.TryGetProperty("attributes", out var relAttrs) && ExtractLocalizedString(relAttrs, "title", out var titles))
                return titles.FirstOrDefault();
        }

        return null;
    }

    private static string? FindRelationshipId(JsonElement item, string relationshipType)
    {
        if (!item.TryGetProperty("relationships", out var relationships) || relationships.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var rel in relationships.EnumerateArray())
        {
            if (GetString(rel, "type") == relationshipType)
                return GetString(rel, "id");
        }

        return null;
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        var http = CreateClient();
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BookmarkManager/2.0");

        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Logger.LogWarning("MangaDex returned non-success code: {Status} for {Url}", response.StatusCode, url);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static LibraryEntryDto? MapManga(JsonElement item, string providerName)
    {
        if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return null;

        var id = idEl.GetString()!;
        if (!item.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Object)
            return null;

        var titlePairs = new List<(string Locale, string Value)>();
        if (attrs.TryGetProperty("title", out var titleObj) && titleObj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in titleObj.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { Length: > 0 } v)
                    titlePairs.Add((prop.Name, v));
            }
        }

        if (attrs.TryGetProperty("altTitles", out var altTitlesEl) && altTitlesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var altTitleObj in altTitlesEl.EnumerateArray())
            {
                foreach (var prop in altTitleObj.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { Length: > 0 } v)
                        titlePairs.Add((prop.Name, v));
                }
            }
        }

        if (titlePairs.Count == 0)
            return null;

        // MangaDex's canonical `title` is often the original-language title, with the English
        // name only present in `altTitles` as its own locale-tagged entry — search both before
        // falling back to whichever locale came first.
        var primaryTitle = titlePairs.FirstOrDefault(p => p.Locale is "en" or "en-us").Value
            ?? titlePairs[0].Value;

        var titles = titlePairs.Select(p => p.Value).Distinct().ToList();
        var alternateTitles = titles.Where(t => !string.Equals(t, primaryTitle, StringComparison.Ordinal)).Take(5).ToList();

        var synopsis = ExtractLocalizedString(attrs, "description", out var descValues) ? descValues.FirstOrDefault() : null;

        // MangaDex tags span 5 groups (genre, theme, format, content, demographic) - all are useful
        // filter facets (e.g. "Isekai"/"Reincarnation" are "theme", "Josei"/"Shounen" are "demographic"),
        // so no group is excluded here.
        var genres = new List<string>();
        if (attrs.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tagsEl.EnumerateArray())
            {
                if (!tag.TryGetProperty("attributes", out var tagAttrs))
                    continue;
                if (tagAttrs.TryGetProperty("name", out var nameEl) && ExtractLocalizedString(nameEl, out var names) && names.FirstOrDefault() is { } genre && !genres.Contains(genre))
                    genres.Add(genre);
            }
        }

        var status = GetString(attrs, "status");
        var latestChapter = GetString(attrs, "lastChapter");
        var latestVolume = GetString(attrs, "lastVolume");
        var originalLanguage = GetString(attrs, "originalLanguage");
        var mediaType = string.Equals(originalLanguage, "ko", StringComparison.OrdinalIgnoreCase)
            ? LibraryMediaType.Manhwa
            : LibraryMediaType.Manga;

        var coverFileName = FindRelationshipAttribute(item, "cover_art", "fileName");
        var coverUrl = coverFileName is not null ? $"https://uploads.mangadex.org/covers/{id}/{coverFileName}.256.jpg" : null;

        var authors = new List<string>();
        var authorName = FindRelationshipAttribute(item, "author", "name");
        if (!string.IsNullOrWhiteSpace(authorName))
            authors.Add(authorName);

        return new LibraryEntryDto(
            providerName,
            id,
            primaryTitle,
            alternateTitles,
            authors,
            mediaType,
            coverUrl,
            synopsis,
            genres,
            null,
            status,
            latestChapter,
            latestVolume,
            null,
            $"https://mangadex.org/title/{id}");
    }

    private static string? FindRelationshipAttribute(JsonElement item, string relationshipType, string attributeName)
    {
        if (!item.TryGetProperty("relationships", out var relationships) || relationships.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var rel in relationships.EnumerateArray())
        {
            if (GetString(rel, "type") != relationshipType)
                continue;
            if (rel.TryGetProperty("attributes", out var relAttrs) && relAttrs.TryGetProperty(attributeName, out var valueEl) && valueEl.ValueKind == JsonValueKind.String)
                return valueEl.GetString();
        }

        return null;
    }

    private static bool ExtractLocalizedString(JsonElement parent, string propertyName, out List<string> values)
    {
        values = [];
        if (!parent.TryGetProperty(propertyName, out var localizedObj))
            return false;
        return ExtractLocalizedString(localizedObj, out values);
    }

    private static bool ExtractLocalizedString(JsonElement localizedObj, out List<string> values)
    {
        values = [];
        if (localizedObj.ValueKind != JsonValueKind.Object)
            return false;

        foreach (var preferred in new[] { "en", "en-us" })
        {
            if (localizedObj.TryGetProperty(preferred, out var preferredEl) && preferredEl.ValueKind == JsonValueKind.String && preferredEl.GetString() is { Length: > 0 } value)
                values.Add(value);
        }

        foreach (var prop in localizedObj.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { Length: > 0 } value && !values.Contains(value))
                values.Add(value);
        }

        return values.Count > 0;
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
