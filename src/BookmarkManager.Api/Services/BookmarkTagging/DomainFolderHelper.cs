using BookmarkManager.Api.Data;
using BookmarkManager.Contracts;
using Microsoft.EntityFrameworkCore;

namespace BookmarkManager.Api.Services.BookmarkTagging;

/// <summary>
/// Maps a media domain (Anime/Manga/Novel) to the user's matching bookmark folder so
/// newly created bookmarks can be filed into the correct folder automatically.
/// Two signals, in priority order:
/// 1. Content evidence — classify the URLs already sitting in each folder and pick
///    the folder where the domain clearly dominates (the user's demonstrated filing
///    behavior; works for folders named anything, e.g. "TFT").
/// 2. Folder-title heuristic — the same title keywords as the tagging classifier
///    (<see cref="BookmarkTagClassifier.GuessDefaultDomainFromFolderTitle"/>), so a
///    "Noveller"/"Light Novels" folder counts as novel regardless of language.
/// </summary>
public static class DomainFolderHelper
{
    /// <summary>Minimum same-domain bookmarks a folder must hold before its contents count as evidence.</summary>
    public const int MinContentEvidence = 5;

    /// <summary>
    /// Returns the folder to file <paramref name="domain"/> bookmarks into: the
    /// content-evidence winner when one folder clearly dominates, otherwise the
    /// first title-matching folder (root-level preferred, then by position). Only
    /// folders with a confirmed <see cref="BookmarkNode.BrowserNodeId"/> qualify so
    /// the move command can be dispatched to the extension immediately. Null when
    /// the user has no folder for the domain (the bookmark stays where it was created).
    /// </summary>
    public static async Task<BookmarkNode?> FindDomainFolderAsync(
        AppDbContext db,
        BookmarkTagDomain domain,
        CancellationToken ct)
    {
        var dto = domain switch
        {
            BookmarkTagDomain.Anime => BookmarkTagDomainDto.Anime,
            BookmarkTagDomain.Manga => BookmarkTagDomainDto.Manga,
            BookmarkTagDomain.Novel => BookmarkTagDomainDto.Novel,
            _ => (BookmarkTagDomainDto?)null
        };
        if (dto is null)
            return null;

        var folders = await db.BookmarkNodes
            .Where(n => n.Type == NodeType.Folder && !n.IsDeleted && n.BrowserNodeId != null)
            .ToListAsync(ct);

        // The system's dead-link triage folder is never an auto-file target.
        var candidates = folders
            .Where(f => !string.Equals(f.Title, Services.BrokenLinksFolderHelper.FolderName, StringComparison.Ordinal))
            .ToList();

        return await FindByContentAsync(db, domain, candidates, ct)
            ?? FindByTitle(dto.Value, candidates);
    }

    /// <summary>
    /// Picks the folder where bookmarks of <paramref name="domain"/> demonstrably live:
    /// highest same-domain URL count, requiring at least <see cref="MinContentEvidence"/>
    /// hits and a majority among the folder's classifiable bookmarks.
    /// </summary>
    private static async Task<BookmarkNode?> FindByContentAsync(
        AppDbContext db,
        BookmarkTagDomain domain,
        List<BookmarkNode> candidates,
        CancellationToken ct)
    {
        var candidateIds = candidates.Select(f => f.Id).ToHashSet();
        var urls = await db.BookmarkNodes
            .Where(n => n.Type == NodeType.Bookmark && !n.IsDeleted && n.ParentId != null && n.Url != null)
            .Select(n => new { n.ParentId, Url = n.Url! })
            .ToListAsync(ct);

        var scores = new Dictionary<Guid, (int Domain, int Classified)>();
        foreach (var bookmark in urls)
        {
            if (bookmark.ParentId is null || !candidateIds.Contains(bookmark.ParentId.Value))
                continue;

            var bookmarkDomain = BookmarkTagClassifier.Classify(string.Empty, bookmark.Url, folderPath: null, BookmarkTagDomainDto.Auto).Domain;
            if (bookmarkDomain is not (BookmarkTagDomain.Anime or BookmarkTagDomain.Manga or BookmarkTagDomain.Novel))
                continue;

            var score = scores.GetValueOrDefault(bookmark.ParentId.Value);
            score.Classified++;
            if (bookmarkDomain == domain)
                score.Domain++;
            scores[bookmark.ParentId.Value] = score;
        }

        var bestFolderId = scores
            .Where(kv => kv.Value.Domain >= MinContentEvidence && kv.Value.Domain * 2 > kv.Value.Classified)
            .OrderByDescending(kv => kv.Value.Domain)
            .Select(kv => (Guid?)kv.Key)
            .FirstOrDefault();

        return bestFolderId is null ? null : candidates.First(f => f.Id == bestFolderId.Value);
    }

    private static BookmarkNode? FindByTitle(BookmarkTagDomainDto dto, List<BookmarkNode> candidates)
        => candidates
            .Where(f => BookmarkTagClassifier.GuessDefaultDomainFromFolderTitle(f.Title) == dto)
            .OrderBy(f => f.ParentId is null ? 0 : 1)
            .ThenBy(f => f.Position)
            .FirstOrDefault();

    /// <summary>
    /// True when the bookmark's current parent folder already signals the same
    /// domain — the bookmark is then considered correctly filed and is not moved
    /// (prevents churn between two folders of the same domain).
    /// </summary>
    public static bool CurrentFolderMatchesDomain(string? parentFolderTitle, BookmarkTagDomain domain)
    {
        var parentDomain = BookmarkTagClassifier.GuessDefaultDomainFromFolderTitle(parentFolderTitle ?? string.Empty);
        return parentDomain.ToString() == domain.ToString();
    }
}
