using System;
using System.Collections.Generic;

namespace BookmarkManager.Contracts;

public class BulkSaveTagsRequest
{
    public Dictionary<Guid, List<string>> Tags { get; set; } = new();

    // Only bookmarks whose title the user actually edited on the review page.
    // Null/omitted preserves old behavior: no title writes.
    public Dictionary<Guid, string>? Titles { get; set; }

    // Bookmark ids whose tags the user manually touched on the review page; those
    // rows get TagProvenance "Manual", untouched rows get the provider source
    // ("Suggested"). Null preserves old behavior: every row in Tags is "Manual".
    public HashSet<Guid>? ManuallyEditedTagIds { get; set; }
}
