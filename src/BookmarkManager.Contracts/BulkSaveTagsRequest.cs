using System;
using System.Collections.Generic;

namespace BookmarkManager.Contracts;

public class BulkSaveTagsRequest
{
    public Dictionary<Guid, List<string>> Tags { get; set; } = new();
}
