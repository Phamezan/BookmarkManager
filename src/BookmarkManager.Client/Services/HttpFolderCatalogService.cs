using BookmarkManager.Contracts;

namespace BookmarkManager.Client.Services;

public sealed class HttpFolderCatalogService : IFolderCatalogService
{
    private readonly IBookmarkManagerApiClient _apiClient;

    public HttpFolderCatalogService(IBookmarkManagerApiClient apiClient)
    {
        _apiClient = apiClient;
    }

    public async Task<IReadOnlyList<FolderCandidateDto>> GetCandidatesAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await _apiClient.GetAsync<List<FolderCatalogNodeDto>>(
            "api/catalog/folders", cancellationToken) ?? [];

        var capturedAt = DateTime.UtcNow;
        return nodes
            .Select(node => new FolderCandidateDto(
                node.BrowserNodeId,
                node.ParentBrowserNodeId,
                node.Title,
                node.Position,
                node.IsProtected,
                IsTracked: false,
                capturedAt))
            .ToList();
    }
}
