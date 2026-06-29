namespace BookmarkManager.Client.Services;

public interface IFolderCatalogService
{
    Task<IReadOnlyList<FolderCandidateDto>> GetCandidatesAsync(CancellationToken cancellationToken = default);
}
