using System.Globalization;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Http;

namespace BookmarkManager.Api.Services.Novels;

/// <summary>
/// Lenient filter/sort set for <c>GET /api/novels/catalog</c>.
/// Invalid query values are ignored and fall back to defaults (never 400).
/// </summary>
public sealed record NovelCatalogFilter
{
    public const int MaxListItems = 40;
    public const int PageSize = 30;

    public const string SortPopular = "popular";
    public const string SortRating = "rating";
    public const string SortUpdated = "updated";
    public const string SortChapters = "chapters";
    public const string SortTitle = "title";

    private static readonly HashSet<string> ValidSorts = new(StringComparer.OrdinalIgnoreCase)
    {
        SortPopular, SortRating, SortUpdated, SortChapters, SortTitle
    };

    public string? Query { get; init; }
    public IReadOnlyList<string> Statuses { get; init; } = [];
    public IReadOnlyList<string> Genres { get; init; } = [];
    public IReadOnlyList<string> ExcludeGenres { get; init; } = [];
    public LibraryMediaType? Type { get; init; }
    public double? MinRating { get; init; }
    public int? MinChapters { get; init; }
    public int? MaxChapters { get; init; }
    public string Sort { get; init; } = SortPopular;
    public bool Readable { get; init; } = true;
    public bool HideLibrary { get; init; }
    public bool HideDismissed { get; init; } = true;
    public int Page { get; init; } = 1;

    public static NovelCatalogFilter FromQuery(IQueryCollection query)
    {
        string? Get(string key) => query.TryGetValue(key, out var v) ? v.ToString() : null;

        var rawQ = Get("q")?.Trim();
        var q = string.IsNullOrEmpty(rawQ)
            ? null
            : rawQ.Length <= NovelPathValidator.MaxQueryLength
                ? rawQ
                : rawQ[..NovelPathValidator.MaxQueryLength];

        var excludeGenres = ParseList(Get("excludeGenres"));
        var excludeSet = new HashSet<string>(excludeGenres, StringComparer.OrdinalIgnoreCase);
        var genres = ParseList(Get("genres"))
            .Where(g => !excludeSet.Contains(g))
            .ToList();

        var sortRaw = Get("sort")?.Trim();
        var sort = sortRaw is not null && ValidSorts.Contains(sortRaw)
            ? sortRaw.ToLowerInvariant()
            : SortPopular;

        return new NovelCatalogFilter
        {
            Query = q,
            Statuses = ParseStatuses(Get("status")),
            Genres = genres,
            ExcludeGenres = excludeGenres,
            Type = ParseType(Get("type")),
            MinRating = ParseDouble(Get("minRating"), 0, 10),
            MinChapters = ParseInt(Get("minChapters"), 0, 1_000_000),
            MaxChapters = ParseInt(Get("maxChapters"), 0, 1_000_000),
            Sort = sort,
            Readable = ParseBool(Get("readable"), fallback: true),
            HideLibrary = ParseBool(Get("hideLibrary"), fallback: false),
            HideDismissed = ParseBool(Get("hideDismissed"), fallback: true),
            Page = ParseInt(Get("page"), 1, 10_000) ?? 1
        };
    }

    public static bool ParseBool(string? raw, bool fallback)
        => bool.TryParse(raw?.Trim(), out var value) ? value : fallback;

    public static bool IsCompletedStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return false;
        }

        return CanonicalizeStatus(status) == "completed";
    }

    public static bool MatchesStatuses(string? rowStatus, IReadOnlyList<string> filterStatuses)
    {
        if (filterStatuses.Count == 0)
        {
            return true;
        }

        var canonical = CanonicalizeStatus(rowStatus);
        if (canonical is null)
        {
            return false;
        }

        foreach (var wanted in filterStatuses)
        {
            if (string.Equals(canonical, wanted, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static string? CanonicalizeStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "ongoing" or "releasing" => "ongoing",
            "completed" or "finished" => "completed",
            "hiatus" or "on hiatus" => "hiatus",
            "cancelled" or "canceled" or "dropped" => "cancelled",
            _ => null
        };
    }

    private static List<string> ParseStatuses(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CanonicalizeStatus)
            .Where(s => s is not null)
            .Select(s => s!)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxListItems)
            .ToList();
    }

    private static LibraryMediaType? ParseType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "lightnovel" => LibraryMediaType.LightNovel,
            "webnovel" => LibraryMediaType.Webnovel,
            _ => null
        };
    }

    private static List<string> ParseList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => v.Length > 0 && v.Length <= NovelPathValidator.MaxQueryLength)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxListItems)
            .ToList();
    }

    private static int? ParseInt(string? raw, int min, int max)
        => int.TryParse(raw?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
           && value >= min && value <= max
            ? value
            : null;

    private static double? ParseDouble(string? raw, double min, double max)
        => double.TryParse(raw?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
           && !double.IsNaN(value) && !double.IsInfinity(value)
           && value >= min && value <= max
            ? value
            : null;
}
