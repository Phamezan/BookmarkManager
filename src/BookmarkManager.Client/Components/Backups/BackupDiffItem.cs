namespace BookmarkManager.Client.Components;

public class BackupDiffItem
{
    public string Title { get; set; } = string.Empty;
    public string? Url { get; set; }
    public string Type { get; set; } = "Bookmark"; // Bookmark or Folder
    public string Action { get; set; } = "Create"; // Create, Update, Delete, Skip
    public string Details { get; set; } = string.Empty; // e.g. "Url changed from 'A' to 'B'"
}
