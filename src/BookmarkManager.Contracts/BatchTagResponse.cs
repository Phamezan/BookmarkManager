using System;
using System.Collections.Generic;

namespace BookmarkManager.Contracts;

public class BatchTagResponse
{
    public Dictionary<Guid, List<string>> Tags { get; set; } = new();
    public Dictionary<Guid, string?> SuggestedTitles { get; set; } = new();
    public Dictionary<Guid, List<TagScoreDto>> TagScores { get; set; } = new();
}
