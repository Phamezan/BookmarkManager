namespace BookmarkManager.Api.Services.BookmarkTagging;

public interface IAnilistTagProvider
{
    Task<List<string>> GetTagsForTitleAsync(string title, string? url, BookmarkTagDomain domain, CancellationToken cancellationToken);
}

public interface IMangaUpdatesTagProvider
{
    Task<List<string>> GetTagsForTitleAsync(string title, string? url, BookmarkTagDomain domain, CancellationToken cancellationToken);
}
