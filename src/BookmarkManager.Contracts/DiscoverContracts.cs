namespace BookmarkManager.Contracts;

/// <summary>One week of the Discover feed: Action series with chapters in the requested window.</summary>
public class DiscoverFeedDto
{
    public DateTime WeekStart { get; set; }
    public DateTime WeekEnd { get; set; }

    /// <summary>True when any stored chapter is older than <see cref="WeekStart"/> (the Older pager is enabled).</summary>
    public bool HasOlder { get; set; }

    public DateTime? LastRunAt { get; set; }
    public List<DiscoverItemDto> Items { get; set; } = [];
}

public class DiscoverItemDto
{
    public string TitleKey { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>Manhwa / Manhua / Manga / Unknown.</summary>
    public string Type { get; set; } = "Unknown";

    public int CoverMangaId { get; set; }

    /// <summary>Manga id of the source with the newest chapter in the window; drives the series link.</summary>
    public int SeriesMangaId { get; set; }

    /// <summary>Distinct source names, newest-updating first.</summary>
    public List<string> Sources { get; set; } = [];

    public List<DiscoverChapterDto> Chapters { get; set; } = [];

    public DiscoverProgressDto? Progress { get; set; }
}

public class DiscoverChapterDto
{
    public double Number { get; set; }
    public string Name { get; set; } = string.Empty;
    public DateTime UploadedAt { get; set; }
    public int MangaId { get; set; }
    public int SourceOrder { get; set; }

    /// <summary>Uploaded within the last 7 days (shown as a NEW pill).</summary>
    public bool IsNew { get; set; }
}

public class DiscoverProgressDto
{
    public int Read { get; set; }
    public int Latest { get; set; }
}

public class DiscoverStatusDto
{
    public DateTime? LastRunAt { get; set; }
    public bool Running { get; set; }
    public List<DiscoverSourceStatusDto> Sources { get; set; } = [];
}

public class DiscoverSourceStatusDto
{
    public string Name { get; set; } = string.Empty;

    /// <summary>ok / skipped / error.</summary>
    public string State { get; set; } = string.Empty;

    public string? Detail { get; set; }
}
