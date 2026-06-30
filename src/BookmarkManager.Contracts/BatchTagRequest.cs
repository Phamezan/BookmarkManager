using System.Collections.Generic;

namespace BookmarkManager.Contracts;

public class BatchTagRequest
{
    public List<BookmarkTagCandidateDto> Items { get; set; } = [];
    public Guid? FolderId { get; set; }
    public string? FolderPath { get; set; }
    public BookmarkTagDomainDto Domain { get; set; } = BookmarkTagDomainDto.Auto;
}
