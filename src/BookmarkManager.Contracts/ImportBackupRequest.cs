using System.Collections.Generic;

namespace BookmarkManager.Contracts;

public class ImportBackupRequest
{
    public List<BookmarkNodeDto> Nodes { get; set; } = new();
    public bool Overwrite { get; set; }
}
