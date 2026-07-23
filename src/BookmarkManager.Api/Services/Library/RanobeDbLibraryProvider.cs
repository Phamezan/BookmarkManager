using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace BookmarkManager.Api.Services.Library;

/// <summary>RanobeDB REST provider - Japanese light novels (published volume/release data, VNDB-style
/// schema), no API key or auth required. Fills the LightNovel gap AniList/MangaDex leave sparse: AniList's
/// MANGA-type catalog crawl only surfaces novel-format entries as a low-popularity slice of manga results,
/// and MangaDex excludes novels entirely. Does not cover raw/fan-translated web novels (Webnovel type) -
/// RanobeDB tracks official print/digital light novel releases, not web serial chapters.</summary>
public sealed class RanobeDbLibraryProvider(
    IHttpClientFactory httpFactory,
    IMemoryCache cache,
    ILogger<RanobeDbLibraryProvider> logger)
    : LibraryMediaProviderBase(httpFactory, cache, logger), IMediaProvider, IBulkCatalogProvider
{
    private const string BaseUrl = "https://ranobedb.org/api/v0";
    private const string ImageBaseUrl = "https://images.ranobedb.org";
    private const int CatalogPageSize = 100;

    // Documented as "no rate limits, but please do not exceed 60 requests in 1 minute."
    private static readonly ProviderRateLimiter RateLimiter = new(
        tokenLimit: 10,
        tokensPerPeriod: 1,
        replenishmentPeriod: TimeSpan.FromSeconds(1.2));

    private static readonly TimeSpan SearchCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DetailsCacheTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan CatalogCacheTtl = TimeSpan.FromHours(6);

    public override string ProviderName => "RanobeDB";

    public bool IsEnabled => true;

    public Task<IReadOnlyList<LibraryEntryDto>> SearchAsync(string query, LibraryMediaType? mediaType, CancellationToken cancellationToken)
    {
        var cleanQuery = query.Trim();
        if (cleanQuery.Length < 2 || (mediaType is not null && mediaType != LibraryMediaType.LightNovel))
            return Task.FromResult<IReadOnlyList<LibraryEntryDto>>([]);

        var cacheKey = $"{ProviderName}:search:{cleanQuery.ToLowerInvariant()}";
        var url = $"{BaseUrl}/series?q={Uri.EscapeDataString(cleanQuery)}&limit=12";

        return ExecuteAsync(
            cacheKey,
            SearchCacheTtl,
            TimeSpan.FromSeconds(5),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
                return (IReadOnlyList<LibraryEntryDto>)ParseSeriesArray(doc, ProviderName);
            },
            [],
            cancellationToken);
    }

    /// <summary>Walks every series (~22k at the time of writing), ordered by volume count descending as
    /// a stand-in for popularity/significance since RanobeDB exposes no follower/trending signal. No
    /// documented depth limit, so this sequence runs unbounded until a page returns fewer than
    /// <see cref="CatalogPageSize"/> results or <c>currentPage</c> reaches <c>totalPages</c>.</summary>
    public IReadOnlyList<string> CatalogMediaTypeQueries { get; } = ["series"];

    public Task<CatalogPageResult> GetCatalogPageAsync(string mediaTypeQuery, string? continuationToken, CancellationToken cancellationToken)
    {
        var page = continuationToken is not null && int.TryParse(continuationToken, out var parsedPage) ? parsedPage : 1;
        var cacheKey = $"{ProviderName}:catalog:{page}";
        var url = $"{BaseUrl}/series?limit={CatalogPageSize}&page={page}&sort={Uri.EscapeDataString("Num. books desc")}";

        return ExecuteCatalogAsync(
            cacheKey,
            CatalogCacheTtl,
            TimeSpan.FromSeconds(15),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
                if (doc is null)
                    throw new HttpRequestException($"RanobeDB catalog page {page} request failed.");
                var entries = ParseSeriesArray(doc, ProviderName);

                var currentPage = doc.RootElement.TryGetProperty("currentPage", out var curEl) && curEl.ValueKind == JsonValueKind.Number
                    ? curEl.GetInt32()
                    : page;
                var totalPages = doc.RootElement.TryGetProperty("totalPages", out var totEl) && totEl.ValueKind == JsonValueKind.Number
                    ? totEl.GetInt32()
                    : currentPage;

                var next = entries.Count < CatalogPageSize || currentPage >= totalPages
                    ? null
                    : (currentPage + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                var rankBase = (page - 1) * CatalogPageSize;
                return new CatalogPageResult(entries, next, rankBase);
            },
            cancellationToken);
    }

    public Task<LibraryEntryDto?> GetDetailsAsync(string providerId, CancellationToken cancellationToken)
    {
        var cacheKey = $"{ProviderName}:details:{providerId}";
        var url = $"{BaseUrl}/series/{Uri.EscapeDataString(providerId)}";

        return ExecuteAsync(
            cacheKey,
            DetailsCacheTtl,
            TimeSpan.FromSeconds(10),
            async ct =>
            {
                await RateLimiter.WaitAsync(ct).ConfigureAwait(false);
                using var doc = await GetJsonAsync(url, ct).ConfigureAwait(false);
                if (doc is null || !doc.RootElement.TryGetProperty("series", out var series) || series.ValueKind != JsonValueKind.Object)
                    return null;

                return MapSeriesDetails(series, ProviderName);
            },
            null,
            cancellationToken);
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
            Logger.LogWarning("RanobeDB returned non-success code: {Status} for {Url}", response.StatusCode, url);
            return null;
        }

        return await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static List<LibraryEntryDto> ParseSeriesArray(JsonDocument? doc, string providerName)
    {
        if (doc is null || !doc.RootElement.TryGetProperty("series", out var seriesArray) || seriesArray.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<LibraryEntryDto>();
        foreach (var item in seriesArray.EnumerateArray())
        {
            var entry = MapSeriesSummary(item, providerName);
            if (entry is not null)
                results.Add(entry);
        }

        return results;
    }

    /// <summary>Maps a row from the list endpoints (<c>/series</c>) - only carries a cover thumbnail and
    /// volume count, not the full tag/staff/description data <see cref="MapSeriesDetails"/> exposes.</summary>
    public static LibraryEntryDto? MapSeriesSummary(JsonElement item, string providerName)
    {
        if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
            return null;

        var id = idEl.GetInt32();
        var title = GetString(item, "title");
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var titleOrig = GetString(item, "title_orig");
        var romajiOrig = GetString(item, "romaji_orig");
        var alternateTitles = new List<string>();
        foreach (var alt in new[] { romajiOrig, titleOrig })
        {
            if (!string.IsNullOrWhiteSpace(alt) && !string.Equals(alt, title, StringComparison.Ordinal) && !alternateTitles.Contains(alt))
                alternateTitles.Add(alt);
        }

        var coverUrl = item.TryGetProperty("book", out var bookEl) && bookEl.ValueKind == JsonValueKind.Object
            ? ExtractImageUrl(bookEl)
            : null;

        var numBooks = item.TryGetProperty("c_num_books", out var numBooksEl) && numBooksEl.ValueKind == JsonValueKind.Number
            ? numBooksEl.GetInt32()
            : (int?)null;

        var lastReleaseAt = item.TryGetProperty("c_end_date", out var endEl) && endEl.ValueKind == JsonValueKind.Number
            ? ParseCompactDate(endEl.GetInt32())
            : null;
        lastReleaseAt ??= item.TryGetProperty("c_start_date", out var startEl) && startEl.ValueKind == JsonValueKind.Number
            ? ParseCompactDate(startEl.GetInt32())
            : null;

        return new LibraryEntryDto(
            providerName,
            id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            title,
            alternateTitles,
            [],
            LibraryMediaType.LightNovel,
            coverUrl,
            null,
            [],
            null,
            null,
            null,
            numBooks?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            lastReleaseAt,
            BuildSourceUrl(id));
    }

    /// <summary>Maps the full <c>/series/{id}</c> response - has tags, staff, publication status, and the
    /// full book list (for cover + latest release date), none of which the list endpoints return.</summary>
    public static LibraryEntryDto? MapSeriesDetails(JsonElement series, string providerName)
    {
        if (!series.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number)
            return null;

        var id = idEl.GetInt32();
        var title = GetString(series, "title");
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var alternateTitles = new List<string>();
        string? englishFromTitles = null;
        if (series.TryGetProperty("titles", out var titlesEl) && titlesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in titlesEl.EnumerateArray())
            {
                var value = GetString(t, "title");
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var lang = GetString(t, "lang");
                if (lang is "en" or "en-us")
                    englishFromTitles ??= value;

                if (!string.Equals(value, title, StringComparison.Ordinal) && !alternateTitles.Contains(value))
                    alternateTitles.Add(value);
            }
        }

        // List/detail `title` is usually the licensed English release, but when it isn't and
        // `titles[]` carries an explicit lang=en entry, prefer that over the original-language primary.
        if (!string.IsNullOrWhiteSpace(englishFromTitles)
            && !string.Equals(englishFromTitles, title, StringComparison.Ordinal))
        {
            if (!alternateTitles.Contains(title))
                alternateTitles.Insert(0, title);
            title = englishFromTitles;
        }

        string? synopsis = null;
        if (series.TryGetProperty("book_description", out var bookDescEl) && bookDescEl.ValueKind == JsonValueKind.Object)
            synopsis = GetString(bookDescEl, "description");
        if (string.IsNullOrWhiteSpace(synopsis))
            synopsis = GetString(series, "description");

        // RanobeDB tags carry a "ttype" of genre/demographic/content - "technical" is metadata noise
        // (e.g. "Translated", "Ongoing") rather than a filterable trait, so it's the only type excluded.
        var genres = new List<string>();
        if (series.TryGetProperty("tags", out var tagsEl) && tagsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var tag in tagsEl.EnumerateArray())
            {
                var ttype = GetString(tag, "ttype");
                if (ttype == "technical")
                    continue;
                if (GetString(tag, "name") is { Length: > 0 } name && !genres.Contains(name))
                    genres.Add(name);
            }
        }

        var authors = new List<string>();
        if (series.TryGetProperty("staff", out var staffEl) && staffEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var staff in staffEl.EnumerateArray())
            {
                var role = GetString(staff, "role_type");
                if (role != "author" && role != "artist")
                    continue;
                var name = GetString(staff, "romaji") ?? GetString(staff, "name");
                if (!string.IsNullOrWhiteSpace(name) && !authors.Contains(name))
                    authors.Add(name);
            }
        }

        string? coverUrl = null;
        int? latestVolumeNumber = null;
        DateTimeOffset? latestReleaseAt = null;
        if (series.TryGetProperty("books", out var booksEl) && booksEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var book in booksEl.EnumerateArray())
            {
                var sortOrder = book.TryGetProperty("sort_order", out var sortEl) && sortEl.ValueKind == JsonValueKind.Number
                    ? sortEl.GetInt32()
                    : (int?)null;

                if (sortOrder == 1 && coverUrl is null)
                    coverUrl = ExtractImageUrl(book);

                var releaseAt = book.TryGetProperty("c_release_date", out var relEl) && relEl.ValueKind == JsonValueKind.Number
                    ? ParseCompactDate(relEl.GetInt32())
                    : null;

                if (releaseAt is { } parsed && (latestReleaseAt is null || parsed > latestReleaseAt))
                {
                    latestReleaseAt = parsed;
                    latestVolumeNumber = sortOrder;
                }
            }
        }

        var numBooks = series.TryGetProperty("books", out var booksArrEl) && booksArrEl.ValueKind == JsonValueKind.Array
            ? booksArrEl.GetArrayLength()
            : (int?)null;

        double? rating = series.TryGetProperty("rating", out var ratingEl) && ratingEl.ValueKind == JsonValueKind.Object &&
                          ratingEl.TryGetProperty("score", out var scoreEl) && scoreEl.ValueKind == JsonValueKind.Number
            ? Math.Round(scoreEl.GetDouble(), 1)
            : null;

        var status = GetString(series, "publication_status");

        return new LibraryEntryDto(
            providerName,
            id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            title,
            alternateTitles,
            authors,
            LibraryMediaType.LightNovel,
            coverUrl,
            synopsis,
            genres,
            rating,
            status,
            null,
            (latestVolumeNumber ?? numBooks)?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            latestReleaseAt,
            BuildSourceUrl(id));
    }

    private static string? ExtractImageUrl(JsonElement parent)
    {
        if (!parent.TryGetProperty("image", out var imageEl) || imageEl.ValueKind != JsonValueKind.Object)
            return null;

        var filename = GetString(imageEl, "filename");
        return string.IsNullOrWhiteSpace(filename) ? null : $"{ImageBaseUrl}/{filename}";
    }

    private static string BuildSourceUrl(int id) => $"https://ranobedb.org/series/{id}";

    /// <summary>RanobeDB (VNDB-style) dates are packed as an 8-digit YYYYMMDD int; MM/DD of <c>99</c> means
    /// "unknown month/day" and a value of <c>99999999</c> means "no end date" (ongoing). Unknown day falls
    /// back to the 1st; an unknown month (or an otherwise malformed value) is treated as no date at all.</summary>
    public static DateTimeOffset? ParseCompactDate(int value)
    {
        if (value <= 0 || value == 99999999)
            return null;

        var year = value / 10000;
        var month = value / 100 % 100;
        var day = value % 100;

        if (year < 1 || month is < 1 or > 12)
            return null;
        if (day is < 1 or > 31)
            day = 1;

        try
        {
            return new DateTimeOffset(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
}
