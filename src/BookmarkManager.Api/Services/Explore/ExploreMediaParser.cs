using System.Text.Json;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.Explore;

/// <summary>Turns AniList media JSON into Explore DTOs. Tolerant of missing / null fields.</summary>
public static class ExploreMediaParser
{
    private const int CardTagCount = 6;
    private const int MaxStaff = 6;
    private const int MaxRecommendations = 12;

    private static readonly HashSet<string> RelationTypes = new(StringComparer.Ordinal)
    {
        "SEQUEL", "PREQUEL", "SIDE_STORY", "SPIN_OFF", "ALTERNATIVE", "PARENT", "SOURCE", "ADAPTATION"
    };

    /// <summary>Parses one media object; null when it has no id (or is not an object).</summary>
    public static ExploreItemDto? ParseItem(JsonElement media)
    {
        if (media.ValueKind != JsonValueKind.Object || Int(media, "id") is not { } id)
        {
            return null;
        }

        var english = Str(Obj(media, "title"), "english");
        var romaji = Str(Obj(media, "title"), "romaji");
        var cover = Obj(media, "coverImage");

        return new ExploreItemDto
        {
            AnilistId = id,
            Title = english ?? romaji ?? Str(Obj(media, "title"), "native") ?? string.Empty,
            RomajiTitle = romaji,
            NativeTitle = Str(Obj(media, "title"), "native"),
            Synonyms = StrArray(media, "synonyms"),
            CoverUrl = Str(cover, "large"),
            BannerUrl = Str(media, "bannerImage"),
            Color = Str(cover, "color"),
            Type = ExploreTypes.FromCountry(Str(media, "countryOfOrigin")),
            Format = Str(media, "format"),
            Status = Str(media, "status"),
            Description = ExploreHtmlText.Strip(Str(media, "description")),
            Genres = StrArray(media, "genres"),
            Tags = ParseTags(media).Take(CardTagCount).ToList(),
            AverageScore = Int(media, "averageScore"),
            Popularity = Int(media, "popularity"),
            Favourites = Int(media, "favourites"),
            Chapters = Int(media, "chapters"),
            Volumes = Int(media, "volumes"),
            StartYear = Int(Obj(media, "startDate"), "year"),
            SiteUrl = Str(media, "siteUrl")
        };
    }

    /// <summary>All non-adult-skipped items of a media array; adult items are dropped unless allowed.</summary>
    public static List<ExploreItemDto> ParseItems(JsonElement mediaArray, bool allowAdult)
    {
        var items = new List<ExploreItemDto>();
        if (mediaArray.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var media in mediaArray.EnumerateArray())
        {
            if (!allowAdult && Bool(media, "isAdult"))
            {
                continue;
            }

            if (ParseItem(media) is { } item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    public static ExploreSeriesDetailDto? ParseDetail(JsonElement media)
    {
        var item = ParseItem(media);
        if (item is null)
        {
            return null;
        }

        return new ExploreSeriesDetailDto
        {
            AnilistId = item.AnilistId,
            Title = item.Title,
            RomajiTitle = item.RomajiTitle,
            NativeTitle = item.NativeTitle,
            Synonyms = item.Synonyms,
            CoverUrl = item.CoverUrl,
            BannerUrl = item.BannerUrl,
            Color = item.Color,
            Type = item.Type,
            Format = item.Format,
            Status = item.Status,
            Description = item.Description,
            Genres = item.Genres,
            Tags = ParseTags(media).ToList(),
            AverageScore = item.AverageScore,
            Popularity = item.Popularity,
            Favourites = item.Favourites,
            Chapters = item.Chapters,
            Volumes = item.Volumes,
            StartYear = item.StartYear,
            SiteUrl = item.SiteUrl,
            StartDate = FormatDate(Obj(media, "startDate")),
            EndDate = FormatDate(Obj(media, "endDate")),
            Staff = ParseStaff(media),
            Relations = ParseRelations(media),
            Recommendations = ParseRecommendations(media),
            ExternalLinks = ParseLinks(media)
        };
    }

    /// <summary>yyyy-MM-dd, yyyy-MM or yyyy depending on which parts AniList knows; null when no year.</summary>
    public static string? FormatDate(JsonElement date)
    {
        if (Int(date, "year") is not { } year)
        {
            return null;
        }

        if (Int(date, "month") is not { } month)
        {
            return $"{year:D4}";
        }

        return Int(date, "day") is { } day ? $"{year:D4}-{month:D2}-{day:D2}" : $"{year:D4}-{month:D2}";
    }

    /// <summary>Non-spoiler, non-adult tag names, highest rank first.</summary>
    public static IEnumerable<string> ParseTags(JsonElement media)
    {
        if (!media.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return tags.EnumerateArray()
            .Where(t => t.ValueKind == JsonValueKind.Object && !Bool(t, "isMediaSpoiler") && !Bool(t, "isAdult"))
            .Select(t => (Name: Str(t, "name"), Rank: Int(t, "rank") ?? 0))
            .Where(t => !string.IsNullOrWhiteSpace(t.Name))
            .OrderByDescending(t => t.Rank)
            .Select(t => t.Name!);
    }

    private static List<ExploreStaffDto> ParseStaff(JsonElement media)
    {
        var edges = Obj(Obj(media, "staff"), "edges");
        if (edges.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return edges.EnumerateArray()
            .Select(e => new ExploreStaffDto { Name = Str(Obj(Obj(e, "node"), "name"), "full") ?? string.Empty, Role = Str(e, "role") })
            .Where(s => s.Name.Length > 0)
            .Take(MaxStaff)
            .ToList();
    }

    private static List<ExploreRelationDto> ParseRelations(JsonElement media)
    {
        var edges = Obj(Obj(media, "relations"), "edges");
        var relations = new List<ExploreRelationDto>();
        if (edges.ValueKind != JsonValueKind.Array)
        {
            return relations;
        }

        foreach (var edge in edges.EnumerateArray())
        {
            var type = Str(edge, "relationType");
            var node = Obj(edge, "node");
            if (type is null || !RelationTypes.Contains(type) || Str(node, "type") != "MANGA" || Bool(node, "isAdult"))
            {
                continue;
            }

            if (ParseItem(node) is { } item)
            {
                relations.Add(new ExploreRelationDto { RelationType = type, Item = item });
            }
        }

        return relations;
    }

    private static List<ExploreItemDto> ParseRecommendations(JsonElement media)
    {
        var nodes = Obj(Obj(media, "recommendations"), "nodes");
        var items = new List<ExploreItemDto>();
        if (nodes.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var node in nodes.EnumerateArray())
        {
            var rec = Obj(node, "mediaRecommendation");
            if (!Bool(rec, "isAdult") && ParseItem(rec) is { } item)
            {
                items.Add(item);
            }
        }

        return items.Take(MaxRecommendations).ToList();
    }

    private static List<ExploreLinkDto> ParseLinks(JsonElement media)
    {
        var links = Obj(media, "externalLinks");
        if (links.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return links.EnumerateArray()
            .Select(l => new ExploreLinkDto { Site = Str(l, "site") ?? string.Empty, Url = Str(l, "url") ?? string.Empty })
            .Where(l => l.Site.Length > 0 && l.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    internal static JsonElement Obj(JsonElement parent, string name)
        => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var value)
            ? value
            : default;

    internal static string? Str(JsonElement parent, string name)
    {
        var value = Obj(parent, name);
        return value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;
    }

    internal static int? Int(JsonElement parent, string name)
    {
        var value = Obj(parent, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
    }

    internal static bool Bool(JsonElement parent, string name)
        => Obj(parent, name).ValueKind == JsonValueKind.True;

    internal static List<string> StrArray(JsonElement parent, string name)
    {
        var value = Obj(parent, name);
        return value.ValueKind != JsonValueKind.Array
            ? []
            : value.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                .Select(e => e.GetString()!)
                .ToList();
    }
}
