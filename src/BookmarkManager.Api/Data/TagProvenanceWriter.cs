namespace BookmarkManager.Api.Data;

/// <summary>
/// Single write path for TagProvenance rows: replaces all provenance for a
/// bookmark in one unit so a bookmark never accumulates stale or duplicate rows.
/// Callers are responsible for SaveChanges.
/// </summary>
internal static class TagProvenanceWriter
{
    public static void Replace(
        AppDbContext db,
        Guid bookmarkId,
        IEnumerable<(string Tag, string Provider, double? MatchScore, string? MatchedTitle)> tags,
        double? confidence)
    {
        // Remove rows added earlier in this unit of work (a delete query cannot
        // see unsaved adds), then the persisted rows.
        var pendingLocal = db.TagProvenances.Local
            .Where(p => p.BookmarkId == bookmarkId)
            .ToList();
        db.TagProvenances.RemoveRange(pendingLocal);
        db.TagProvenances.RemoveRange(db.TagProvenances.Where(p => p.BookmarkId == bookmarkId));

        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, provider, matchScore, matchedTitle) in tags)
        {
            var trimmed = tag?.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || !seen.Add(trimmed))
                continue;

            db.TagProvenances.Add(new TagProvenance
            {
                BookmarkId = bookmarkId,
                Tag = trimmed,
                Provider = provider,
                Confidence = confidence,
                MatchScore = matchScore,
                MatchedTitle = matchedTitle,
                CreatedAt = now
            });
        }
    }
}
