namespace BookmarkManager.Api.Services.BookmarkTagging;

public record ProviderTagResult(List<string> Tags, bool WasRejected, string? RejectionReason);

public interface IAnilistTagProvider
{
    Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken);
}

public interface IMangaUpdatesTagProvider
{
    Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken);
}

public interface IKitsuTagProvider
{
    Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken);
}

public interface INovelFullTagProvider
{
    Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken);
}

public interface INovelUpdatesTagProvider
{
    Task<ProviderTagResult> GetTagsForTitleAsync(MediaTagLookupContext context, CancellationToken cancellationToken);
}
