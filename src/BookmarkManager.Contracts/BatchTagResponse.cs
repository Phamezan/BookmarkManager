using System;
using System.Collections.Generic;

namespace BookmarkManager.Contracts;

public class BatchTagResponse
{
    public Dictionary<Guid, List<string>> Tags { get; set; } = new();
}
