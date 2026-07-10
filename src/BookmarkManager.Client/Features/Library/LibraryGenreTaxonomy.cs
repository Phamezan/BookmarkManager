namespace BookmarkManager.Client.Features.Library;

/// <summary>Groups the flat genre/tag strings providers return into browsable sections so the
/// Library filter row is scannable instead of one endless chip cloud.</summary>
public static class LibraryGenreTaxonomy
{
    public sealed record GenreGroup(string Label, IReadOnlyList<string> Tags);

    private static readonly (string Label, string[] Tags)[] Groups =
    [
        ("Action & adventure", ["Action", "Adventure", "Martial Arts", "Military", "Wuxia", "Xianxia", "Xuanhuan", "Mecha", "War", "Survival"]),
        ("Fantasy & isekai", ["Fantasy", "Isekai", "Magic", "Supernatural", "Reincarnation", "Transmigration", "Cultivation", "System", "Demons", "Monsters"]),
        ("Romance & drama", ["Romance", "Drama", "Harem", "Josei", "Shoujo", "Shounen", "Yaoi", "Yuri", "Love Polygon", "Tragedy"]),
        ("Comedy & slice of life", ["Comedy", "Slice of Life", "School", "Cooking", "Food", "Sports"]),
        ("Mystery & thriller", ["Mystery", "Horror", "Psychological", "Thriller", "Crime", "Detective"]),
        ("Sci-fi & modern", ["Sci-Fi", "Cyberpunk", "Post-Apocalyptic", "Virtual Reality", "Game", "Modern", "Futuristic", "Space"]),
        ("Character & tone", ["Anti-Hero", "Male Protagonist", "Female Protagonist", "Strong Lead", "Weak Lead", "Smart Lead", "Adaptation", "Historical"]),
    ];

    private static readonly Dictionary<string, string> TagToGroup =
        Groups.SelectMany(g => g.Tags.Select(t => (Tag: t, g.Label)))
              .ToDictionary(x => x.Tag, x => x.Label, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<GenreGroup> GroupGenres(IEnumerable<string> genres)
    {
        var available = genres
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (available.Count == 0)
            return [];

        var buckets = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var other = new List<string>();

        foreach (var genre in available.OrderBy(g => g, StringComparer.OrdinalIgnoreCase))
        {
            if (TagToGroup.TryGetValue(genre, out var group))
            {
                if (!buckets.TryGetValue(group, out var list))
                {
                    list = [];
                    buckets[group] = list;
                }

                list.Add(genre);
            }
            else
            {
                other.Add(genre);
            }
        }

        var result = new List<GenreGroup>();
        foreach (var (label, _) in Groups)
        {
            if (buckets.TryGetValue(label, out var tags) && tags.Count > 0)
                result.Add(new GenreGroup(label, tags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList()));
        }

        if (other.Count > 0)
            result.Add(new GenreGroup("Other tags", other));

        return result;
    }
}
