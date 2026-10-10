using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services.Suwayomi;

/// <summary>
/// Outcome of matching an AniList search response against a normalized query. <see cref="Titles"/>
/// holds every known title of the accepted media (romaji, english, native, synonyms), so callers can
/// register alternative-title aliases. A miss is <see cref="Found"/> false with no titles.
/// </summary>
public sealed record SeriesMetadataLookup(
    bool Found,
    int? AnilistId,
    string? CountryOfOrigin,
    IReadOnlyList<string> Titles);

/// <summary>
/// Resolves a series title to its AniList identity (id, country of origin, alternative titles) and
/// caches the result in <see cref="SeriesMetadata"/>. A positive match is cached indefinitely; a
/// negative match is cached for <see cref="MissRetention"/>. Transient AniList failures are not
/// cached so the next run retries them. All AniList calls share
/// <see cref="AnilistTaggingService.RateLimiter"/> with the auto-tagging provider.
/// </summary>
public sealed class SeriesMetadataService
{
    private static readonly TimeSpan MissRetention = TimeSpan.FromDays(30);

    private const string LookupQuery =
        @"query ($s: String) {
            Page(perPage: 5) {
              media(search: $s, type: MANGA) {
                id
                countryOfOrigin
                title { romaji english native }
                synonyms
              }
            }
          }";

    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SeriesMetadataService> _logger;

    public SeriesMetadataService(
        AppDbContext db,
        IHttpClientFactory httpFactory,
        ILogger<SeriesMetadataService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>
    /// Returns the cached row for <paramref name="title"/> if it is a hit or a recent miss; otherwise
    /// queries AniList and upserts the row. Returns null for an empty key and for a transient AniList
    /// failure (nothing is written so the next run retries).
    /// </summary>
    public async Task<SeriesMetadata?> GetOrLookupAsync(string title, CancellationToken ct)
    {
        var key = MediaTitleNormalizer.NormalizeForSearch(title);
        if (key.Length == 0)
        {
            return null;
        }

        var existing = await _db.SeriesMetadataSet
            .FirstOrDefaultAsync(m => m.NormalizedKey == key, ct)
            .ConfigureAwait(false);
        if (existing is not null && IsFresh(existing, DateTime.UtcNow))
        {
            return existing;
        }

        SeriesMetadataLookup lookup;
        try
        {
            await AnilistTaggingService.RateLimiter.WaitAsync(ct).ConfigureAwait(false);

            var http = _httpFactory.CreateClient(nameof(AnilistTaggingService));
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BookmarkManager/2.0");

            using var resp = await http.PostAsJsonAsync(
                "https://graphql.anilist.co", CreateGraphQlBody(title), ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "AniList series metadata lookup for '{Title}' returned {Status}; not caching.",
                    title, resp.StatusCode);
                return null;
            }

            using var doc = await resp.Content
                .ReadFromJsonAsync<JsonDocument>(cancellationToken: ct).ConfigureAwait(false);
            lookup = ExtractLookup(doc?.RootElement, title);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AniList series metadata lookup for '{Title}' failed.", title);
            return null;
        }

        var row = existing ?? new SeriesMetadata { Id = Guid.NewGuid(), NormalizedKey = key };
        row.AnilistId = lookup.AnilistId;
        row.CountryOfOrigin = lookup.CountryOfOrigin;
        row.TitlesJson = lookup.Found ? SerializeTitles(lookup.Titles) : null;
        row.Found = lookup.Found;
        row.LookedUpAt = DateTime.UtcNow;
        if (existing is null)
        {
            _db.SeriesMetadataSet.Add(row);
        }

        await _db.SaveChangesAsync(ct).ConfigureAwait(false);
        return row;
    }

    /// <summary>A row needs no re-query if it is a hit or a miss still inside <see cref="MissRetention"/>.</summary>
    internal static bool IsFresh(SeriesMetadata metadata, DateTime now)
        => metadata.Found || metadata.LookedUpAt > now - MissRetention;

    public static object CreateGraphQlBody(string search) => new
    {
        query = LookupQuery,
        variables = new { s = search }
    };

    /// <summary>Reads <c>data.Page.media</c> out of an AniList response and runs the acceptance check.</summary>
    public static SeriesMetadataLookup ExtractLookup(JsonElement? root, string query)
    {
        if (root is not { } element ||
            !element.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("Page", out var page) ||
            !page.TryGetProperty("media", out var media))
        {
            return new SeriesMetadataLookup(false, null, null, []);
        }

        return SelectAccepted(query, media);
    }

    /// <summary>
    /// Parses the media array straight from JSON, so the acceptance rule is unit-testable against a
    /// captured AniList fixture without any HTTP plumbing.
    /// </summary>
    public static SeriesMetadataLookup SelectAccepted(string query, string mediaArrayJson)
    {
        using var doc = JsonDocument.Parse(mediaArrayJson);
        return SelectAccepted(query, doc.RootElement);
    }

    /// <summary>
    /// Returns the first media whose romaji/english/native title or one of its synonyms normalizes
    /// exactly to the query. No fuzzy acceptance: a wrong match would put a wrong badge on Discover.
    /// An empty or non-array media list is a miss.
    /// </summary>
    public static SeriesMetadataLookup SelectAccepted(string query, JsonElement mediaArray)
    {
        var normalizedQuery = MediaTitleNormalizer.NormalizeForSearch(query);
        if (normalizedQuery.Length == 0 || mediaArray.ValueKind != JsonValueKind.Array)
        {
            return new SeriesMetadataLookup(false, null, null, []);
        }

        foreach (var media in mediaArray.EnumerateArray())
        {
            if (media.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var titles = ExtractTitles(media);
            var accepted = titles.Any(t => MediaTitleNormalizer.NormalizeForSearch(t) == normalizedQuery);
            if (!accepted)
            {
                continue;
            }

            var anilistId = media.TryGetProperty("id", out var idEl) && idEl.TryGetInt32(out var id) ? id : (int?)null;
            var country = media.TryGetProperty("countryOfOrigin", out var countryEl) && countryEl.ValueKind == JsonValueKind.String
                ? countryEl.GetString()
                : null;
            return new SeriesMetadataLookup(true, anilistId, country, titles);
        }

        return new SeriesMetadataLookup(false, null, null, []);
    }

    /// <summary>romaji + english + native + every synonym, non-empty and de-duplicated in order.</summary>
    public static IReadOnlyList<string> ExtractTitles(JsonElement media)
    {
        var titles = new List<string>();
        if (media.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "romaji", "english", "native" })
            {
                AddTitle(titles, titleEl, key);
            }
        }

        if (media.TryGetProperty("synonyms", out var synonymsEl) && synonymsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var synonym in synonymsEl.EnumerateArray())
            {
                if (synonym.ValueKind == JsonValueKind.String)
                {
                    AddTitle(titles, synonym.GetString());
                }
            }
        }

        return titles;
    }

    public static string? SerializeTitles(IReadOnlyList<string> titles)
        => titles.Count == 0 ? null : JsonSerializer.Serialize(titles);

    /// <summary>Parses a stored <see cref="SeriesMetadata.TitlesJson"/>; empty on null/garbage.</summary>
    public static IReadOnlyList<string> ParseTitles(string? titlesJson)
    {
        if (string.IsNullOrWhiteSpace(titlesJson))
        {
            return [];
        }

        try
        {
            using var doc = JsonDocument.Parse(titlesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return doc.RootElement.EnumerateArray()
                .Where(el => el.ValueKind == JsonValueKind.String)
                .Select(el => el.GetString()!)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void AddTitle(List<string> titles, JsonElement parent, string property)
    {
        if (parent.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String)
        {
            AddTitle(titles, el.GetString());
        }
    }

    private static void AddTitle(List<string> titles, string? value)
    {
        var trimmed = value?.Trim();
        if (!string.IsNullOrEmpty(trimmed)
            && !titles.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            titles.Add(trimmed);
        }
    }
}
