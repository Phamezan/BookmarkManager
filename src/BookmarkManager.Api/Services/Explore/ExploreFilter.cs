using Microsoft.AspNetCore.Http;

namespace BookmarkManager.Api.Services.Explore;

/// <summary>The Browse/Random filter set. Immutable; <c>with</c> builds the shelf variants.</summary>
public sealed record ExploreFilter
{
    public const int MaxListItems = 40;
    public const int MaxTextLength = 200;

    private static readonly IReadOnlyDictionary<string, string> SortMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["popularity"] = "POPULARITY_DESC",
            ["score"] = "SCORE_DESC",
            ["trending"] = "TRENDING_DESC",
            ["newest"] = "START_DATE_DESC",
            ["updated"] = "UPDATED_AT_DESC",
            ["title"] = "TITLE_ENGLISH",
            ["chapters"] = "CHAPTERS_DESC",
            ["favourites"] = "FAVOURITES_DESC",
            ["recentlyFinished"] = "END_DATE_DESC"
        };

    private static readonly HashSet<string> Statuses = new(StringComparer.Ordinal)
    {
        "FINISHED", "RELEASING", "HIATUS", "CANCELLED", "NOT_YET_RELEASED"
    };

    public string? Query { get; init; }
    public string Type { get; init; } = "all";
    public IReadOnlyList<string> Status { get; init; } = [];
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> ExcludeGenres { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> ExcludeTags { get; init; } = [];
    public int? MinScore { get; init; }
    public int? MinChapters { get; init; }
    public int? MaxChapters { get; init; }
    public int? YearFrom { get; init; }
    public int? YearTo { get; init; }

    /// <summary>Canonical sort key (a key of the sort map) or null when none was requested.</summary>
    public string? Sort { get; init; }

    public int? MaxPopularity { get; init; }
    public bool Adult { get; init; }
    public bool HideLibrary { get; init; }
    public bool HideDismissed { get; init; } = true;
    public int Page { get; init; } = 1;

    /// <summary>Resolved AniList sort: explicit sort, else SEARCH_MATCH when searching, else popularity.</summary>
    public string ResolvedSort()
    {
        if (Sort is not null && SortMap.TryGetValue(Sort, out var mapped))
        {
            return mapped;
        }

        return string.IsNullOrWhiteSpace(Query) ? "POPULARITY_DESC" : "SEARCH_MATCH";
    }

    /// <summary>Lenient parse: any invalid value is ignored and falls back to its default.</summary>
    public static ExploreFilter FromQuery(IQueryCollection query)
    {
        string? Get(string key) => query.TryGetValue(key, out var v) ? v.ToString() : null;

        var q = Get("q")?.Trim();
        var type = Get("type")?.Trim().ToLowerInvariant();
        var sortRaw = Get("sort")?.Trim();
        var sort = SortMap.Keys.FirstOrDefault(k => string.Equals(k, sortRaw, StringComparison.OrdinalIgnoreCase));

        return new ExploreFilter
        {
            Query = string.IsNullOrEmpty(q) ? null : Truncate(q),
            Type = type is "manhwa" or "manhua" or "manga" ? type : "all",
            Status = ParseList(Get("status"), Statuses.Contains, upper: true),
            Genres = ParseList(Get("genres")),
            ExcludeGenres = ParseList(Get("excludeGenres")),
            Tags = ParseList(Get("tags")),
            ExcludeTags = ParseList(Get("excludeTags")),
            MinScore = ParseInt(Get("minScore"), 0, 100),
            MinChapters = ParseInt(Get("minChapters"), 0, 100000),
            MaxChapters = ParseInt(Get("maxChapters"), 0, 100000),
            YearFrom = ParseInt(Get("yearFrom"), 1900, 2200),
            YearTo = ParseInt(Get("yearTo"), 1900, 2200),
            Sort = sort,
            MaxPopularity = ParseInt(Get("maxPopularity"), 1, int.MaxValue),
            Adult = ParseBool(Get("adult"), false),
            HideLibrary = ParseBool(Get("hideLibrary"), false),
            HideDismissed = ParseBool(Get("hideDismissed"), true),
            Page = ParseInt(Get("page"), 1, 1000) ?? 1
        };
    }

    private static string Truncate(string value) => value.Length <= MaxTextLength ? value : value[..MaxTextLength];

    private static int? ParseInt(string? raw, int min, int max)
        => int.TryParse(raw, out var value) && value >= min && value <= max ? value : null;

    private static bool ParseBool(string? raw, bool fallback)
        => bool.TryParse(raw, out var value) ? value : fallback;

    private static List<string> ParseList(string? raw, Func<string, bool>? accept = null, bool upper = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => upper ? v.ToUpperInvariant() : v)
            .Where(v => v.Length > 0 && v.Length <= MaxTextLength && (accept is null || accept(v)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxListItems)
            .ToList();
    }
}
