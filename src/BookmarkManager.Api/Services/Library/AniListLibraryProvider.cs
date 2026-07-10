using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.Library;

/// <summary>AniList GraphQL provider - covers anime and manga, no API key required.</summary>
public sealed partial class AniListLibraryProvider(
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    ILogger<AniListLibraryProvider> logger)
    : LibraryMediaProviderBase(httpFactory, cache, logger), IMediaProvider, ITrendingMediaProvider, IBulkCatalogProvider
{
    private const string Endpoint = "https://graphql.anilist.co";
    private const int CatalogPageSize = 50;
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DetailsCacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan TrendingCacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan CatalogCacheTtl = TimeSpan.FromHours(6);

    private static readonly ProviderRateLimiter RateLimiter = new(
        tokenLimit: 8,
        tokensPerPeriod: 2,
        replenishmentPeriod: TimeSpan.FromSeconds(5));

    public override string ProviderName => "AniList";

    public bool IsEnabled => true;

    public Task<IReadOnlyList<LibraryEntryDto>> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken)
    {
        var cleanQuery = query.Trim();
        if (cleanQuery.Length < 2)
            return Task.FromResult<IReadOnlyList<LibraryEntryDto>>([]);

        var aniListType = mediaType == LibraryMediaType.Anime ? "ANIME" : "MANGA";
        var cacheKey = $"{ProviderName}:search:{aniListType}:{cleanQuery.ToLowerInvariant()}";

        return ExecuteAsync(
            cacheKey,
            SearchCacheTtl,
            TimeSpan.FromSeconds(5),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                using var doc = await QueryAsync(BuildSearchBody(cleanQuery, aniListType), ct).ConfigureAwait(false);
                return doc is null
                    ? (IReadOnlyList<LibraryEntryDto>)[]
                    : ParseSearchResults(doc.RootElement, ProviderName);
            },
            [],
            cancellationToken);
    }

    public Task<IReadOnlyList<LibraryEntryDto>> GetTrendingAsync(LibraryMediaType? mediaType, CancellationToken cancellationToken)
    {
        var aniListType = mediaType == LibraryMediaType.Anime ? "ANIME" : "MANGA";
        var cacheKey = $"{ProviderName}:trending:{aniListType}";

        return ExecuteAsync(
            cacheKey,
            TrendingCacheTtl,
            TimeSpan.FromSeconds(8),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                using var doc = await QueryAsync(BuildTrendingBody(aniListType), ct).ConfigureAwait(false);
                return doc is null
                    ? (IReadOnlyList<LibraryEntryDto>)[]
                    : ParseSearchResults(doc.RootElement, ProviderName);
            },
            [],
            cancellationToken);
    }

    public IReadOnlyList<string> CatalogMediaTypeQueries { get; } = ["ANIME", "MANGA"];

    /// <summary>Walks AniList's full catalog in POPULARITY_DESC order, one page at a time. AniList has no
    /// documented depth limit on page-based pagination (unlike MangaDex's 10,000 offset ceiling), so this
    /// sequence runs unbounded (see <see cref="LibraryCatalogSyncQueueItem.RemainingPages"/>) until a page
    /// returns fewer than <see cref="CatalogPageSize"/> results.</summary>
    public Task<CatalogPageResult> GetCatalogPageAsync(string mediaTypeQuery, string? continuationToken, CancellationToken cancellationToken)
    {
        var page = continuationToken is not null && int.TryParse(continuationToken, out var parsedPage) ? parsedPage : 1;
        var cacheKey = $"{ProviderName}:catalog:{mediaTypeQuery}:{page}";

        return ExecuteAsync(
            cacheKey,
            CatalogCacheTtl,
            TimeSpan.FromSeconds(15),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                using var doc = await QueryAsync(BuildCatalogPageBody(mediaTypeQuery, page, CatalogPageSize), ct).ConfigureAwait(false);
                var entries = doc is null
                    ? (IReadOnlyList<LibraryEntryDto>)[]
                    : ParseSearchResults(doc.RootElement, ProviderName);
                var next = entries.Count < CatalogPageSize
                    ? null
                    : (page + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var rankBase = (page - 1) * CatalogPageSize;
                return new CatalogPageResult(entries, next, rankBase);
            },
            new CatalogPageResult([], null),
            cancellationToken);
    }

    public Task<LibraryEntryDto?> GetDetailsAsync(string providerId, CancellationToken cancellationToken)
    {
        if (!int.TryParse(providerId, out var id))
            return Task.FromResult<LibraryEntryDto?>(null);

        var cacheKey = $"{ProviderName}:details:{id}";

        return ExecuteAsync(
            cacheKey,
            DetailsCacheTtl,
            TimeSpan.FromSeconds(10),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                using var doc = await QueryAsync(BuildDetailsBody(id), ct).ConfigureAwait(false);
                if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("Media", out var media) || media.ValueKind != JsonValueKind.Object)
                    return null;

                return MapMedia(media, ProviderName);
            },
            null,
            cancellationToken);
    }

    public async Task<LibraryReleaseInfo?> GetLatestReleaseAsync(string providerId, CancellationToken cancellationToken)
    {
        var entry = await GetDetailsAsync(providerId, cancellationToken).ConfigureAwait(false);
        return entry is null
            ? null
            : new LibraryReleaseInfo(entry.LatestChapter, entry.LatestVolume, entry.LastReleaseAt, entry.SourceUrl);
    }

    private async Task<JsonDocument?> QueryAsync(object body, CancellationToken cancellationToken)
    {
        var http = CreateClient();
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BookmarkManager/2.0");

        using var response = await http.PostAsJsonAsync(Endpoint, body, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Logger.LogWarning("AniList returned non-success code: {Status}", response.StatusCode);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static object BuildSearchBody(string query, string aniListType)
    {
        const string graphQlQuery = """
            query ($search: String, $type: MediaType) {
              Page(page: 1, perPage: 12) {
                media(search: $search, type: $type) {
                  id
                  type
                  format
                  countryOfOrigin
                  title { romaji english native }
                  coverImage { large }
                  description(asHtml: false)
                  genres
                  averageScore
                  status
                  chapters
                  volumes
                  episodes
                  siteUrl
                  updatedAt
                  staff(perPage: 3, sort: RELEVANCE) {
                    edges { role node { name { full } } }
                  }
                }
              }
            }
            """;

        return new { query = graphQlQuery, variables = new { search = query, type = aniListType } };
    }

    public static object BuildTrendingBody(string aniListType)
    {
        const string graphQlQuery = """
            query ($type: MediaType) {
              Page(page: 1, perPage: 12) {
                media(type: $type, sort: TRENDING_DESC) {
                  id
                  type
                  format
                  countryOfOrigin
                  title { romaji english native }
                  coverImage { large }
                  description(asHtml: false)
                  genres
                  averageScore
                  status
                  chapters
                  volumes
                  episodes
                  siteUrl
                  updatedAt
                  staff(perPage: 3, sort: RELEVANCE) {
                    edges { role node { name { full } } }
                  }
                }
              }
            }
            """;

        return new { query = graphQlQuery, variables = new { type = aniListType } };
    }

    public static object BuildCatalogPageBody(string aniListType, int page, int perPage)
    {
        const string graphQlQuery = """
            query ($type: MediaType, $page: Int, $perPage: Int) {
              Page(page: $page, perPage: $perPage) {
                media(type: $type, sort: POPULARITY_DESC) {
                  id
                  type
                  format
                  countryOfOrigin
                  title { romaji english native }
                  coverImage { large }
                  description(asHtml: false)
                  genres
                  averageScore
                  status
                  chapters
                  volumes
                  episodes
                  siteUrl
                  updatedAt
                  staff(perPage: 3, sort: RELEVANCE) {
                    edges { role node { name { full } } }
                  }
                }
              }
            }
            """;

        return new { query = graphQlQuery, variables = new { type = aniListType, page, perPage } };
    }

    public static object BuildDetailsBody(int id)
    {
        const string graphQlQuery = """
            query ($id: Int) {
              Media(id: $id) {
                id
                type
                format
                countryOfOrigin
                title { romaji english native }
                coverImage { large }
                description(asHtml: false)
                genres
                averageScore
                status
                chapters
                volumes
                episodes
                siteUrl
                updatedAt
                staff(perPage: 3, sort: RELEVANCE) {
                  edges { role node { name { full } } }
                }
              }
            }
            """;

        return new { query = graphQlQuery, variables = new { id } };
    }

    public static IReadOnlyList<LibraryEntryDto> ParseSearchResults(JsonElement root, string providerName)
    {
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Page", out var page) ||
            !page.TryGetProperty("media", out var mediaArray) ||
            mediaArray.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<LibraryEntryDto>();
        foreach (var media in mediaArray.EnumerateArray())
        {
            var entry = MapMedia(media, providerName);
            if (entry is not null)
                results.Add(entry);
        }

        return results;
    }

    public static LibraryEntryDto? MapMedia(JsonElement media, string providerName)
    {
        if (!media.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
            return null;

        var id = idEl.GetInt32();
        var titles = new List<string>();
        string? romaji = null, english = null;
        if (media.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.Object)
        {
            romaji = GetString(titleEl, "romaji");
            english = GetString(titleEl, "english");
            var native = GetString(titleEl, "native");
            foreach (var t in new[] { romaji, english, native })
            {
                if (!string.IsNullOrWhiteSpace(t) && !titles.Contains(t))
                    titles.Add(t);
            }
        }

        var primaryTitle = romaji ?? english ?? titles.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(primaryTitle))
            return null;

        var alternateTitles = titles.Where(t => !string.Equals(t, primaryTitle, StringComparison.Ordinal)).ToList();

        var coverUrl = media.TryGetProperty("coverImage", out var coverEl) && coverEl.ValueKind == JsonValueKind.Object
            ? GetString(coverEl, "large")
            : null;

        var synopsis = GetString(media, "description") is { } rawDescription
            ? StripHtml(rawDescription)
            : null;

        var genres = new List<string>();
        if (media.TryGetProperty("genres", out var genresEl) && genresEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var g in genresEl.EnumerateArray())
            {
                if (g.ValueKind == JsonValueKind.String && g.GetString() is { Length: > 0 } genre)
                    genres.Add(genre);
            }
        }

        double? rating = media.TryGetProperty("averageScore", out var scoreEl) && scoreEl.ValueKind == JsonValueKind.Number
            ? Math.Round(scoreEl.GetDouble() / 10.0, 1)
            : null;

        var status = GetString(media, "status");
        var mediaType = GetString(media, "type");
        var format = GetString(media, "format");
        var country = GetString(media, "countryOfOrigin");

        var libraryType = ResolveMediaType(mediaType, format, country);

        string? latestChapter = null;
        string? latestVolume = null;
        if (libraryType == LibraryMediaType.Anime)
        {
            latestChapter = media.TryGetProperty("episodes", out var epEl) && epEl.ValueKind == JsonValueKind.Number
                ? epEl.GetInt32().ToString()
                : null;
        }
        else
        {
            latestChapter = media.TryGetProperty("chapters", out var chEl) && chEl.ValueKind == JsonValueKind.Number
                ? chEl.GetInt32().ToString()
                : null;
            latestVolume = media.TryGetProperty("volumes", out var volEl) && volEl.ValueKind == JsonValueKind.Number
                ? volEl.GetInt32().ToString()
                : null;
        }

        DateTimeOffset? lastReleaseAt = media.TryGetProperty("updatedAt", out var updatedEl) && updatedEl.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(updatedEl.GetInt64())
            : null;

        var sourceUrl = GetString(media, "siteUrl") ?? $"https://anilist.co/{mediaType?.ToLowerInvariant() ?? "manga"}/{id}";

        var authors = ExtractAuthors(media);

        return new LibraryEntryDto(
            providerName,
            id.ToString(),
            primaryTitle,
            alternateTitles,
            authors,
            libraryType,
            coverUrl,
            synopsis,
            genres,
            rating,
            status,
            latestChapter,
            latestVolume,
            lastReleaseAt,
            sourceUrl);
    }

    private static List<string> ExtractAuthors(JsonElement media)
    {
        var authors = new List<string>();
        if (!media.TryGetProperty("staff", out var staffEl) || !staffEl.TryGetProperty("edges", out var edgesEl) || edgesEl.ValueKind != JsonValueKind.Array)
            return authors;

        foreach (var edge in edgesEl.EnumerateArray())
        {
            var role = GetString(edge, "role") ?? string.Empty;
            if (!role.Contains("Story", StringComparison.OrdinalIgnoreCase) &&
                !role.Contains("Art", StringComparison.OrdinalIgnoreCase) &&
                !role.Contains("Original Creator", StringComparison.OrdinalIgnoreCase))
                continue;

            if (edge.TryGetProperty("node", out var node) && node.TryGetProperty("name", out var nameEl))
            {
                var full = GetString(nameEl, "full");
                if (!string.IsNullOrWhiteSpace(full) && !authors.Contains(full))
                    authors.Add(full);
            }
        }

        return authors;
    }

    public static LibraryMediaType ResolveMediaType(string? aniListType, string? format, string? countryOfOrigin)
    {
        if (string.Equals(aniListType, "ANIME", StringComparison.OrdinalIgnoreCase))
            return LibraryMediaType.Anime;

        if (string.Equals(format, "NOVEL", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(countryOfOrigin, "JP", StringComparison.OrdinalIgnoreCase)
                ? LibraryMediaType.LightNovel
                : LibraryMediaType.Webnovel;
        }

        return string.Equals(countryOfOrigin, "KR", StringComparison.OrdinalIgnoreCase)
            ? LibraryMediaType.Manhwa
            : LibraryMediaType.Manga;
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static string StripHtml(string value)
    {
        var withBreaks = HtmlBreakRegex().Replace(value, "\n");
        var stripped = HtmlTagRegex().Replace(withBreaks, string.Empty);
        return WhitespaceRegex().Replace(stripped, " ").Trim();
    }

    [GeneratedRegex(@"(?i)<br\s*/?>")]
    private static partial Regex HtmlBreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"[ \t]+")]
    private static partial Regex WhitespaceRegex();
}
