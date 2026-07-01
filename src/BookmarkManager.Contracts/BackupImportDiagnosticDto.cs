namespace BookmarkManager.Contracts;

public class BackupImportDiagnosticDto
{
    public string Severity { get; set; } = "Error";
    public string Code { get; set; } = string.Empty;
    public Guid? NodeId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
}
