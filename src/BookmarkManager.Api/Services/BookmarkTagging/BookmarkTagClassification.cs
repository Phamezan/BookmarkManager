namespace BookmarkManager.Api.Services.BookmarkTagging;

public sealed record BookmarkTagClassification(
    BookmarkTagDomain Domain,
    string CleanTitle,
    bool ShouldUseAniList,
    bool ShouldUseMangaUpdates,
    string Reason);
