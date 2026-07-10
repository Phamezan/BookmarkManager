using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.Library;

/// <summary>Kitsu JSON:API provider - anime and manga/manhwa/novel, no API key required.
/// Kitsu ids are only unique per resource type, so <see cref="LibraryEntryDto.ProviderId"/> is
/// encoded as "{resourceType}:{id}" (e.g. "manga:12345") to disambiguate.</summary>
public sealed class KitsuLibraryProvider(
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    ILogger<KitsuLibraryProvider> logger)
    : LibraryMediaProviderBase(httpFactory, cache, logger), IMediaProvider
{
    private const string BaseUrl = "https://kitsu.io/api/edge";
    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DetailsCacheTtl = TimeSpan.FromHours(6);

    public override string ProviderName => "Kitsu";

    public bool IsEnabled => true;

    public Task<IReadOnlyList<LibraryEntryDto>> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken)
    {
        var cleanQuery = query.Trim();
        if (cleanQuery.Length < 2)
            return Task.FromResult<IReadOnlyList<LibraryEntryDto>>([]);

        var resourceType = mediaType == LibraryMediaType.Anime ? "anime" : "manga";
        var cacheKey = $"{ProviderName}:search:{resourceType}:{cleanQuery.ToLowerInvariant()}";
        var url = $"{BaseUrl}/{resourceType}?filter[text]={Uri.EscapeDataString(cleanQuery)}&page[limit]=12&include=categories";

        return ExecuteAsync(
            cacheKey,
            SearchCacheTtl,
            TimeSpan.FromSeconds(5),
            async ct =>
            {
                using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
                if (doc is null || !doc.RootElement.TryGetProperty("data", out var dataArray) || dataArray.ValueKind != JsonValueKind.Array)
                    return (IReadOnlyList<LibraryEntryDto>)[];

                var categoryTitles = ExtractIncludedCategoryTitles(doc.RootElement);
                var results = new List<LibraryEntryDto>();
                foreach (var item in dataArray.EnumerateArray())
                {
                    var entry = MapResource(item, resourceType, ProviderName, categoryTitles);
                    if (entry is not null)
                        results.Add(entry);
                }

                return results;
            },
            [],
            cancellationToken);
    }

    public Task<LibraryEntryDto?> GetDetailsAsync(string providerId, CancellationToken cancellationToken)
    {
        if (!TryParseProviderId(providerId, out var resourceType, out var id))
            return Task.FromResult<LibraryEntryDto?>(null);

        var cacheKey = $"{ProviderName}:details:{providerId}";
        var url = $"{BaseUrl}/{resourceType}/{id}?include=categories";

        return ExecuteAsync(
            cacheKey,
            DetailsCacheTtl,
            TimeSpan.FromSeconds(10),
            async ct =>
            {
                using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
                if (doc is null || !doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                    return null;

                var categoryTitles = ExtractIncludedCategoryTitles(doc.RootElement);
                return MapResource(data, resourceType, ProviderName, categoryTitles);
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

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        var http = CreateClient();
        http.DefaultRequestHeaders.Accept.Clear();
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.api+json"));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("BookmarkManager/2.0");

        using var response = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Logger.LogWarning("Kitsu returned non-success code: {Status} for {Url}", response.StatusCode, url);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static bool TryParseProviderId(string providerId, out string resourceType, out string id)
    {
        resourceType = "manga";
        id = string.Empty;
        var parts = providerId.Split(':', 2);
        if (parts.Length != 2 || parts[0] is not ("anime" or "manga"))
            return false;

        resourceType = parts[0];
        id = parts[1];
        return id.Length > 0;
    }

    /// <summary>Builds a map of Kitsu category resource id -> title from a response's sideloaded
    /// <c>included</c> array (populated when the request has <c>include=categories</c>).</summary>
    public static Dictionary<string, string> ExtractIncludedCategoryTitles(JsonElement root)
    {
        var titles = new Dictionary<string, string>();
        if (!root.TryGetProperty("included", out var includedEl) || includedEl.ValueKind != JsonValueKind.Array)
            return titles;

        foreach (var included in includedEl.EnumerateArray())
        {
            if (GetString(included, "type") != "categories")
                continue;
            if (!included.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                continue;
            if (!included.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Object)
                continue;

            var title = GetString(attrs, "title");
            if (!string.IsNullOrWhiteSpace(title))
                titles[idEl.GetString()!] = title;
        }

        return titles;
    }

    private static List<string> ExtractCategoryGenres(JsonElement item, IReadOnlyDictionary<string, string> categoryTitles)
    {
        var genres = new List<string>();
        if (categoryTitles.Count == 0)
            return genres;
        if (!item.TryGetProperty("relationships", out var relationships) || relationships.ValueKind != JsonValueKind.Object)
            return genres;
        if (!relationships.TryGetProperty("categories", out var categoriesRel) || categoriesRel.ValueKind != JsonValueKind.Object)
            return genres;
        if (!categoriesRel.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return genres;

        foreach (var reference in dataEl.EnumerateArray())
        {
            if (!reference.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                continue;
            if (categoryTitles.TryGetValue(idEl.GetString()!, out var title) && !genres.Contains(title))
                genres.Add(title);
        }

        return genres;
    }

    public static LibraryEntryDto? MapResource(JsonElement item, string resourceType, string providerName)
        => MapResource(item, resourceType, providerName, new Dictionary<string, string>());

    public static LibraryEntryDto? MapResource(JsonElement item, string resourceType, string providerName, IReadOnlyDictionary<string, string> categoryTitles)
    {
        if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return null;
        if (!item.TryGetProperty("attributes", out var attrs) || attrs.ValueKind != JsonValueKind.Object)
            return null;

        var id = idEl.GetString()!;
        var canonicalTitle = GetString(attrs, "canonicalTitle");

        // Kitsu's own "canonical" title is often the romanized native title (romaji/Revised
        // Romanization of Korean), not English. Prefer titles.en when Kitsu has one.
        string? englishTitle = null;
        var allTitles = new List<string>();
        if (attrs.TryGetProperty("titles", out var titlesEl) && titlesEl.ValueKind == JsonValueKind.Object)
        {
            englishTitle = GetString(titlesEl, "en");
            foreach (var prop in titlesEl.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.String && prop.Value.GetString() is { Length: > 0 } t &&
                    !allTitles.Contains(t))
                    allTitles.Add(t);
            }
        }

        var primaryTitle = englishTitle ?? canonicalTitle ?? allTitles.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(primaryTitle))
            return null;

        var alternateTitles = allTitles
            .Concat(canonicalTitle is not null ? [canonicalTitle] : [])
            .Where(t => !string.Equals(t, primaryTitle, StringComparison.Ordinal))
            .Distinct()
            .ToList();

        var synopsis = GetString(attrs, "synopsis");

        string? coverUrl = null;
        if (attrs.TryGetProperty("posterImage", out var posterEl) && posterEl.ValueKind == JsonValueKind.Object)
            coverUrl = GetString(posterEl, "large") ?? GetString(posterEl, "original");

        double? rating = null;
        if (attrs.TryGetProperty("averageRating", out var ratingEl) && ratingEl.ValueKind == JsonValueKind.String &&
            double.TryParse(ratingEl.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ratingValue))
        {
            rating = Math.Round(ratingValue / 10.0, 1);
        }

        var status = GetString(attrs, "status");
        var subtype = GetString(attrs, "subtype");

        var mediaType = resourceType == "anime"
            ? LibraryMediaType.Anime
            : subtype?.ToLowerInvariant() switch
            {
                "manhwa" => LibraryMediaType.Manhwa,
                "novel" => LibraryMediaType.LightNovel,
                _ => LibraryMediaType.Manga
            };

        string? latestChapter = attrs.TryGetProperty("chapterCount", out var chEl) && chEl.ValueKind == JsonValueKind.Number
            ? chEl.GetInt32().ToString()
            : null;
        string? latestVolume = attrs.TryGetProperty("volumeCount", out var volEl) && volEl.ValueKind == JsonValueKind.Number
            ? volEl.GetInt32().ToString()
            : null;
        string? latestEpisode = resourceType == "anime" && attrs.TryGetProperty("episodeCount", out var epEl) && epEl.ValueKind == JsonValueKind.Number
            ? epEl.GetInt32().ToString()
            : null;

        var slug = GetString(attrs, "slug") ?? id;
        var sourceUrl = $"https://kitsu.io/{resourceType}/{slug}";
        var genres = ExtractCategoryGenres(item, categoryTitles);

        return new LibraryEntryDto(
            providerName,
            $"{resourceType}:{id}",
            primaryTitle,
            alternateTitles,
            [],
            mediaType,
            coverUrl,
            synopsis,
            genres,
            rating,
            status,
            resourceType == "anime" ? latestEpisode : latestChapter,
            latestVolume,
            null,
            sourceUrl);
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
