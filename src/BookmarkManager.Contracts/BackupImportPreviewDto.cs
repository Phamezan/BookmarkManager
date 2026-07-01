namespace BookmarkManager.Contracts;

public class BackupImportPreviewDto
{
    public bool Overwrite { get; set; }
    public int CreateCount { get; set; }
    public int UpdateCount { get; set; }
    public int RestoreCount { get; set; }
    public int DeleteCount { get; set; }
    public int SkipCount { get; set; }
    public int MetadataOnlyCount { get; set; }
    public List<BackupImportPreviewItemDto> Items { get; set; } = [];
    public List<BackupImportDiagnosticDto> Diagnostics { get; set; } = [];
    public bool CanImport => Diagnostics.All(d => !string.Equals(d.Severity, "Error", StringComparison.OrdinalIgnoreCase));
}
