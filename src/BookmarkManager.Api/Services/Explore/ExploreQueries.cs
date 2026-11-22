namespace BookmarkManager.Api.Services.Explore;

/// <summary>
/// AniList GraphQL documents and the filter-to-variables mapping. All user input travels as GraphQL
/// variables; the only string interpolation is the constant field fragment below.
/// </summary>
public static class ExploreQueries
{
    public const int BrowsePerPage = 30;
    public const int ShelfPerPage = 18;
    public const int ForYouBatchSize = 50;

    /// <summary>Fields shared by every media selection (cards, relations, recommendations).</summary>
    public const string ItemFields = @"id type countryOfOrigin format status isAdult siteUrl
        title { romaji english native } synonyms
        coverImage { large color } bannerImage description
        genres tags { name rank isMediaSpoiler isAdult }
        averageScore popularity favourites chapters volumes
        startDate { year }";

    public const string Browse = @"query ($page: Int, $perPage: Int, $search: String, $countryOfOrigin: CountryCode,
        $statusIn: [MediaStatus], $genreIn: [String], $genreNotIn: [String], $tagIn: [String], $tagNotIn: [String],
        $averageScoreGreater: Int, $popularityLesser: Int, $chaptersGreater: Int, $chaptersLesser: Int,
        $startDateGreater: FuzzyDateInt, $startDateLesser: FuzzyDateInt, $formatNotIn: [MediaFormat],
        $isAdult: Boolean, $sort: [MediaSort]) {
      Page(page: $page, perPage: $perPage) {
        pageInfo { total lastPage hasNextPage }
        media(type: MANGA, search: $search, countryOfOrigin: $countryOfOrigin, status_in: $statusIn,
              genre_in: $genreIn, genre_not_in: $genreNotIn, tag_in: $tagIn, tag_not_in: $tagNotIn,
              averageScore_greater: $averageScoreGreater, popularity_lesser: $popularityLesser,
              chapters_greater: $chaptersGreater, chapters_lesser: $chaptersLesser,
              startDate_greater: $startDateGreater, startDate_lesser: $startDateLesser,
              format_not_in: $formatNotIn, isAdult: $isAdult, sort: $sort) {
          " + ItemFields + @"
        }
      }
    }";

    public const string Meta = @"query { GenreCollection MediaTagCollection { name category isAdult } }";

    public const string Detail = @"query ($id: Int) {
      Media(id: $id, type: MANGA) {
        " + ItemFields + @"
        startDate { year month day } endDate { year month day }
        staff(perPage: 6, sort: RELEVANCE) { edges { role node { name { full } } } }
        relations { edges { relationType node { " + ItemFields + @" } } }
        recommendations(perPage: 12, sort: RATING_DESC) { nodes { mediaRecommendation { " + ItemFields + @" } } }
        externalLinks { site url }
      }
    }";

    public const string ForYouBatch = @"query ($ids: [Int], $perPage: Int) {
      Page(perPage: $perPage) {
        media(id_in: $ids, type: MANGA) {
          id title { english romaji }
          recommendations(perPage: 10, sort: RATING_DESC) {
            nodes { rating mediaRecommendation { " + ItemFields + @" } }
          }
        }
      }
    }";

    /// <summary>Maps a filter to the Browse query variables. Null / empty values are left out.</summary>
    public static Dictionary<string, object?> BuildBrowseVariables(ExploreFilter filter, int perPage)
    {
        var vars = new Dictionary<string, object?>
        {
            ["page"] = Math.Max(1, filter.Page),
            ["perPage"] = perPage,
            ["formatNotIn"] = new[] { "NOVEL" },
            ["isAdult"] = filter.Adult ? null : false,
            ["sort"] = new[] { filter.ResolvedSort() }
        };

        Set(vars, "search", string.IsNullOrWhiteSpace(filter.Query) ? null : filter.Query);
        Set(vars, "countryOfOrigin", ExploreTypes.ToCountryFilter(filter.Type));
        SetList(vars, "statusIn", filter.Status);
        SetList(vars, "genreIn", filter.Genres);
        SetList(vars, "genreNotIn", filter.ExcludeGenres);
        SetList(vars, "tagIn", filter.Tags);
        SetList(vars, "tagNotIn", filter.ExcludeTags);

        // AniList's *_greater / *_lesser are exclusive, so widen by one to get inclusive bounds.
        if (filter.MinScore is > 0)
        {
            vars["averageScoreGreater"] = filter.MinScore.Value - 1;
        }

        if (filter.MaxPopularity is { } popularity)
        {
            vars["popularityLesser"] = popularity;
        }

        if (filter.MinChapters is > 0)
        {
            vars["chaptersGreater"] = filter.MinChapters.Value - 1;
        }

        if (filter.MaxChapters is { } maxChapters)
        {
            vars["chaptersLesser"] = maxChapters + 1;
        }

        if (filter.YearFrom is { } from)
        {
            vars["startDateGreater"] = (from * 10000) - 1;
        }

        if (filter.YearTo is { } to)
        {
            vars["startDateLesser"] = (to + 1) * 10000;
        }

        return vars.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static void Set(Dictionary<string, object?> vars, string key, string? value)
    {
        if (value is not null)
        {
            vars[key] = value;
        }
    }

    private static void SetList(Dictionary<string, object?> vars, string key, IReadOnlyList<string> values)
    {
        if (values.Count > 0)
        {
            vars[key] = values.ToArray();
        }
    }
}
