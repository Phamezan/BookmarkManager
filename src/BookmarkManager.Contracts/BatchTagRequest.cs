using System.Collections.Generic;

namespace BookmarkManager.Contracts;

public class BatchTagRequest
{
    public List<BookmarkTagCandidateDto> Items { get; set; } = [];
    public bool UseAi { get; set; } = true;
    public bool AllowFallback { get; set; } = true;
}
