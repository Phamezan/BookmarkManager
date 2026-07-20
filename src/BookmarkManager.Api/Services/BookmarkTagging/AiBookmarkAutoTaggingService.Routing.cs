using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;

namespace BookmarkManager.Api.Services.BookmarkTagging;

internal sealed partial class AiBookmarkAutoTaggingService
{
    private static RouteDecision ResolveRoute(
        BookmarkNode bookmark,
        string? folderPath,
        AiSeriesIdentification identification)
    {
        var folderDomain = BookmarkTagClassifier.GuessDefaultDomainFromFolderTitle(folderPath ?? string.Empty);
        if (folderDomain is BookmarkTagDomainDto.Anime or BookmarkTagDomainDto.Manga or BookmarkTagDomainDto.Novel)
            return FromDomainDto(folderDomain, identification.SourceHint);

        var urlClassification = BookmarkTagClassifier.Classify(
            identification.CanonicalTitle,
            bookmark.Url,
            folderPath,
            BookmarkTagDomainDto.Auto);
        if (urlClassification.Domain != BookmarkTagDomain.General)
            return WithMediaSubtype(urlClassification.Domain, identification.SourceHint);

        return FromSourceHint(identification.SourceHint);
    }

    private static RouteDecision ResolveDeterministicRoute(BookmarkNode bookmark, string? folderPath, BookmarkTagDomain domain)
    {
        if (domain != BookmarkTagDomain.Manga)
            return new RouteDecision(domain, domain.ToString());

        var path = folderPath ?? string.Empty;
        var title = bookmark.Title ?? string.Empty;
        if (path.Contains("Manhwa", StringComparison.OrdinalIgnoreCase) || title.Contains("Manhwa", StringComparison.OrdinalIgnoreCase))
            return new RouteDecision(BookmarkTagDomain.Manga, "Manhwa");
        if (path.Contains("Manhua", StringComparison.OrdinalIgnoreCase) || title.Contains("Manhua", StringComparison.OrdinalIgnoreCase))
            return new RouteDecision(BookmarkTagDomain.Manga, "Manhua");
        return new RouteDecision(BookmarkTagDomain.Manga, "Manga");
    }

    private static RouteDecision FromDomainDto(BookmarkTagDomainDto domain, AiSeriesSourceHint sourceHint)
        => domain switch
        {
            BookmarkTagDomainDto.Anime => new RouteDecision(BookmarkTagDomain.Anime, "Anime"),
            BookmarkTagDomainDto.Manga => WithMediaSubtype(BookmarkTagDomain.Manga, sourceHint),
            BookmarkTagDomainDto.Novel => new RouteDecision(BookmarkTagDomain.Novel, "Novel"),
            _ => new RouteDecision(BookmarkTagDomain.General, null)
        };

    private static RouteDecision FromSourceHint(AiSeriesSourceHint sourceHint)
        => sourceHint switch
        {
            AiSeriesSourceHint.Anime => new RouteDecision(BookmarkTagDomain.Anime, "Anime"),
            AiSeriesSourceHint.Manga => new RouteDecision(BookmarkTagDomain.Manga, "Manga"),
            AiSeriesSourceHint.Manhwa => new RouteDecision(BookmarkTagDomain.Manga, "Manhwa"),
            AiSeriesSourceHint.Manhua => new RouteDecision(BookmarkTagDomain.Manga, "Manhua"),
            AiSeriesSourceHint.Novel => new RouteDecision(BookmarkTagDomain.Novel, "Novel"),
            _ => new RouteDecision(BookmarkTagDomain.General, null)
        };

    private static RouteDecision WithMediaSubtype(BookmarkTagDomain domain, AiSeriesSourceHint sourceHint)
    {
        if (domain != BookmarkTagDomain.Manga)
            return new RouteDecision(domain, domain.ToString());

        return sourceHint switch
        {
            AiSeriesSourceHint.Manhwa => new RouteDecision(BookmarkTagDomain.Manga, "Manhwa"),
            AiSeriesSourceHint.Manhua => new RouteDecision(BookmarkTagDomain.Manga, "Manhua"),
            _ => new RouteDecision(BookmarkTagDomain.Manga, "Manga")
        };
    }

    private static List<ProvenanceTagEntry> MergeTags(string mediaTypeTag, IEnumerable<ProvenanceTagEntry> sourceTags)
    {
        var merged = new List<ProvenanceTagEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Add(mediaTypeTag, "DomainRoute", null, null);
        foreach (var entry in sourceTags)
            Add(entry.Tag, entry.Provider, entry.MatchScore, entry.MatchedTitle);

        return merged.Take(MaxTags).ToList();

        void Add(string? tag, string provider, double? matchScore, string? matchedTitle)
        {
            var trimmed = tag?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return;

            if (IsOtherMediaType(trimmed, mediaTypeTag))
                return;

            if (seen.Add(trimmed))
                merged.Add(new ProvenanceTagEntry(trimmed, provider, matchScore, matchedTitle));
        }
    }

    private static bool IsOtherMediaType(string tag, string mediaTypeTag)
    {
        string[] mediaTypes = ["Anime", "Manga", "Manhwa", "Manhua", "Novel"];
        return mediaTypes.Contains(tag, StringComparer.OrdinalIgnoreCase)
            && !tag.Equals(mediaTypeTag, StringComparison.OrdinalIgnoreCase);
    }

    private static HashSet<Guid> GetDescendantFolderIds(IReadOnlyCollection<BookmarkNode> nodes, Guid folderId)
    {
        var folderIds = new HashSet<Guid> { folderId };
        var childrenByParent = nodes
            .Where(node => node.Type == NodeType.Folder && node.ParentId.HasValue)
            .GroupBy(node => node.ParentId!.Value)
            .ToDictionary(group => group.Key, group => group.Select(node => node.Id).ToList());
        var pending = new Queue<Guid>();
        pending.Enqueue(folderId);

        while (pending.Count > 0)
        {
            var parentId = pending.Dequeue();
            if (!childrenByParent.TryGetValue(parentId, out var childIds))
                continue;

            foreach (var childId in childIds)
            {
                if (folderIds.Add(childId))
                    pending.Enqueue(childId);
            }
        }

        return folderIds;
    }

    private static Dictionary<Guid, string> BuildFolderPaths(IEnumerable<BookmarkNode> folders)
    {
        var byId = folders.ToDictionary(folder => folder.Id);
        var paths = new Dictionary<Guid, string>();

        foreach (var folder in byId.Values)
            paths[folder.Id] = BuildPath(folder, byId, paths);

        return paths;
    }

    private static string BuildPath(BookmarkNode folder, IReadOnlyDictionary<Guid, BookmarkNode> folders, Dictionary<Guid, string> cache)
    {
        if (cache.TryGetValue(folder.Id, out var cached))
            return cached;

        var parts = new Stack<string>();
        BookmarkNode? current = folder;
        var seen = new HashSet<Guid>();
        while (current is not null && seen.Add(current.Id))
        {
            if (!string.IsNullOrWhiteSpace(current.Title))
                parts.Push(current.Title.Trim());

            current = current.ParentId.HasValue && folders.TryGetValue(current.ParentId.Value, out var parent)
                ? parent
                : null;
        }

        var path = string.Join(" / ", parts);
        cache[folder.Id] = path;
        return path;
    }

    private static string NormalizeCacheTitle(string title)
        => title.Trim().ToLowerInvariant();
}
