using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;
using Microsoft.Extensions.Caching.Memory;

namespace BookmarkManager.Api.Services.Explore;

/// <summary>One Browse page: parsed items plus AniList's paging info.</summary>
public sealed record ExploreBrowsePage(List<ExploreItemDto> Items, int? Total, int LastPage, bool HasNextPage);

/// <summary>
/// AniList access for the Explore page. Every request waits on the shared AniList rate limiter first,
/// responses are cached in <see cref="IMemoryCache"/> (as raw JSON, so mutable DTOs are never shared
/// between requests) and failures are never cached - they surface as <see cref="ExploreUnavailableException"/>.
/// </summary>
public sealed class ExploreAniListClient
{
    public const string Endpoint = "https://graphql.anilist.co";

    private static readonly TimeSpan BrowseTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DetailTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan MetaTtl = TimeSpan.FromHours(24);

    private readonly IHttpClientFactory _httpFactory;
    private readonly IMemoryCache _cache;
    private readonly ProviderRateLimiter _limiter;
    private readonly ILogger<ExploreAniListClient> _logger;

    public ExploreAniListClient(
        IHttpClientFactory httpFactory,
        IMemoryCache cache,
        ProviderRateLimiter limiter,
        ILogger<ExploreAniListClient> logger)
    {
        _httpFactory = httpFactory;
        _cache = cache;
        _limiter = limiter;
        _logger = logger;
    }

    public async Task<ExploreBrowsePage> BrowseAsync(ExploreFilter filter, int perPage, CancellationToken ct)
    {
        var variables = ExploreQueries.BuildBrowseVariables(filter, perPage);
        var data = await QueryAsync("browse", ExploreQueries.Browse, variables, BrowseTtl, ct).ConfigureAwait(false);

        var page = ExploreMediaParser.Obj(data, "Page");
        var info = ExploreMediaParser.Obj(page, "pageInfo");
        var items = ExploreMediaParser.ParseItems(ExploreMediaParser.Obj(page, "media"), filter.Adult);
        return new ExploreBrowsePage(
            items,
            ExploreMediaParser.Int(info, "total"),
            ExploreMediaParser.Int(info, "lastPage") ?? 1,
            ExploreMediaParser.Obj(info, "hasNextPage").ValueKind == JsonValueKind.True);
    }

    public async Task<ExploreMetaDto> GetMetaAsync(CancellationToken ct)
    {
        var data = await QueryAsync("meta", ExploreQueries.Meta, new Dictionary<string, object?>(), MetaTtl, ct)
            .ConfigureAwait(false);

        var tags = ExploreMediaParser.Obj(data, "MediaTagCollection");
        return new ExploreMetaDto
        {
            // Hentai is AniList's adult-only genre; adult content is opt-in elsewhere, so never offer it as a filter.
            Genres = ExploreMediaParser.StrArray(data, "GenreCollection")
                .Where(g => !string.Equals(g, "Hentai", StringComparison.OrdinalIgnoreCase))
                .ToList(),
            Tags = tags.ValueKind != JsonValueKind.Array
                ? []
                : tags.EnumerateArray()
                    .Where(t => !ExploreMediaParser.Bool(t, "isAdult"))
                    .Select(t => new ExploreTagDto
                    {
                        Name = ExploreMediaParser.Str(t, "name") ?? string.Empty,
                        Category = ExploreMediaParser.Str(t, "category")
                    })
                    .Where(t => t.Name.Length > 0)
                    .ToList()
        };
    }

    /// <summary>Full series detail; null when AniList returns no media for the id.</summary>
    public async Task<ExploreSeriesDetailDto?> GetDetailAsync(int anilistId, CancellationToken ct)
    {
        var data = await QueryAsync(
            "detail", ExploreQueries.Detail, new Dictionary<string, object?> { ["id"] = anilistId }, DetailTtl, ct)
            .ConfigureAwait(false);
        return ExploreMediaParser.ParseDetail(ExploreMediaParser.Obj(data, "Media"));
    }

    /// <summary>The <c>data</c> object of a cached-or-fresh AniList response.</summary>
    internal async Task<JsonElement> QueryAsync(
        string name, string query, Dictionary<string, object?> variables, TimeSpan ttl, CancellationToken ct)
    {
        var key = $"explore:{name}:{JsonSerializer.Serialize(variables)}";
        if (_cache.TryGetValue(key, out JsonElement cached))
        {
            return cached;
        }

        var data = await SendAsync(query, variables, ct).ConfigureAwait(false);
        _cache.Set(key, data, ttl);
        return data;
    }

    private async Task<JsonElement> SendAsync(string query, Dictionary<string, object?> variables, CancellationToken ct)
    {
        try
        {
            await _limiter.WaitAsync(ct).ConfigureAwait(false);

            var http = _httpFactory.CreateClient(nameof(AnilistTaggingService));
            http.DefaultRequestHeaders.Accept.Clear();
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BookmarkManager/2.0");

            using var resp = await http.PostAsJsonAsync(Endpoint, new { query, variables }, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                throw new ExploreUnavailableException($"AniList returned {(int)resp.StatusCode}.");
            }

            using var doc = await resp.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken: ct).ConfigureAwait(false);
            var root = doc?.RootElement ?? default;
            if (root.ValueKind != JsonValueKind.Object
                || ExploreMediaParser.Obj(root, "data") is not { ValueKind: JsonValueKind.Object } data)
            {
                throw new ExploreUnavailableException("AniList returned no data.");
            }

            return data.Clone();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ExploreUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AniList Explore request failed.");
            throw new ExploreUnavailableException("AniList request failed.", ex);
        }
    }
}
