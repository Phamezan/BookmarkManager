namespace BookmarkManager.Contracts;

/// <summary>Reading progress of an Explore series in the user's Suwayomi library.</summary>
public class ExploreLibraryDto
{
    public int MangaId { get; set; }
    public int Read { get; set; }
    public int Latest { get; set; }
    public string? SourceName { get; set; }
}

/// <summary>One AniList manga/manhwa/manhua card on the Explore page.</summary>
public class ExploreItemDto
{
    public int AnilistId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? RomajiTitle { get; set; }
    public string? NativeTitle { get; set; }
    public List<string> Synonyms { get; set; } = [];
    public string? CoverUrl { get; set; }
    public string? BannerUrl { get; set; }
    public string? Color { get; set; }

    /// <summary>Manga, Manhwa, Manhua or Unknown (from AniList country of origin).</summary>
    public string Type { get; set; } = "Unknown";

    public string? Format { get; set; }
    public string? Status { get; set; }

    /// <summary>Plain text; AniList HTML is stripped server-side.</summary>
    public string? Description { get; set; }

    public List<string> Genres { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public int? AverageScore { get; set; }
    public int? Popularity { get; set; }
    public int? Favourites { get; set; }
    public int? Chapters { get; set; }
    public int? Volumes { get; set; }
    public int? StartYear { get; set; }
    public string? SiteUrl { get; set; }
    public ExploreLibraryDto? Library { get; set; }

    /// <summary>"saved", "dismissed" or null.</summary>
    public string? ListState { get; set; }

    /// <summary>For-you only: library titles that led to this recommendation.</summary>
    public List<string> BecauseOf { get; set; } = [];
}

public class ExplorePageDto
{
    public int Page { get; set; }
    public bool HasNextPage { get; set; }
    public int? Total { get; set; }
    public List<ExploreItemDto> Items { get; set; } = [];
}

public class ExploreShelfDto
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;

    /// <summary>Querystring that reproduces the shelf in the Browse grid (for-you: <c>view=for-you</c>).</summary>
    public string BrowseQuery { get; set; } = string.Empty;

    public List<ExploreItemDto> Items { get; set; } = [];
}

public class ExploreStaffDto
{
    public string Name { get; set; } = string.Empty;
    public string? Role { get; set; }
}

public class ExploreRelationDto
{
    public string RelationType { get; set; } = string.Empty;
    public ExploreItemDto Item { get; set; } = new();
}

public class ExploreLinkDto
{
    public string Site { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public class ExploreSeriesDetailDto : ExploreItemDto
{
    /// <summary>yyyy-MM-dd, yyyy-MM or yyyy; null when unknown.</summary>
    public string? StartDate { get; set; }
    public string? EndDate { get; set; }
    public List<ExploreStaffDto> Staff { get; set; } = [];
    public List<ExploreRelationDto> Relations { get; set; } = [];
    public List<ExploreItemDto> Recommendations { get; set; } = [];
    public List<ExploreLinkDto> ExternalLinks { get; set; } = [];
}

public class ExploreSourceHitDto
{
    public int MangaId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public bool InLibrary { get; set; }
    public bool IsExactMatch { get; set; }
}

public class ExploreTagDto
{
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
}

public class ExploreMetaDto
{
    public List<string> Genres { get; set; } = [];
    public List<ExploreTagDto> Tags { get; set; } = [];
}

public class ExploreAddToLibraryRequest
{
    public int MangaId { get; set; }
}

public class ExploreListUpdateRequest
{
    /// <summary>"saved", "dismissed" or null (removes the entry).</summary>
    public string? State { get; set; }
    public string? Title { get; set; }
    public string? CoverUrl { get; set; }
    public string? Type { get; set; }
}
