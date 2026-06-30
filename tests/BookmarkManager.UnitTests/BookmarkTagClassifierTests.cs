using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;

namespace BookmarkManager.UnitTests;

public sealed class BookmarkTagClassifierTests
{
    [Theory]
    [InlineData("Anime", "Frieren - Episode 12", "https://crunchyroll.com/watch/x")]
    [InlineData("Shows/Anime", "One Piece", "https://anilist.co/anime/21")]
    public void Classify_UsesAnimeFolderContext(string folderPath, string title, string url)
    {
        var result = BookmarkTagClassifier.Classify(title, url, folderPath, BookmarkTagDomainDto.Auto);

        Assert.Equal(BookmarkTagDomain.Anime, result.Domain);
        Assert.True(result.ShouldUseAniList);
        Assert.False(result.ShouldUseMangaUpdates);
    }

    [Theory]
    [InlineData("Manga", "Jujutsu Kaisen Chapter 245", "https://mangadex.org/title/jjk")]
    [InlineData("Manhwa", "Solo Leveling - Chapter 1", "https://asuracomic.net/series/solo-leveling")]
    public void Classify_UsesMangaFolderContext(string folderPath, string title, string url)
    {
        var result = BookmarkTagClassifier.Classify(title, url, folderPath, BookmarkTagDomainDto.Auto);

        Assert.Equal(BookmarkTagDomain.Manga, result.Domain);
        Assert.False(result.ShouldUseAniList);
        Assert.True(result.ShouldUseMangaUpdates);
    }

    [Theory]
    [InlineData("Light Novels", "Lord of the Mysteries Chapter 100", "https://novelbin.me/novel-book/lord-of-the-mysteries")]
    [InlineData("Wuxia Novels", "Reverend Insanity", "https://novelupdates.com/series/reverend-insanity")]
    [InlineData("LN", "Classroom of the Elite Volume 1", "https://example.com/classroom-of-the-elite")]
    public void Classify_UsesNovelFolderContext(string folderPath, string title, string url)
    {
        var result = BookmarkTagClassifier.Classify(title, url, folderPath, BookmarkTagDomainDto.Auto);

        Assert.Equal(BookmarkTagDomain.Novel, result.Domain);
        Assert.False(result.ShouldUseAniList);
        Assert.True(result.ShouldUseMangaUpdates);
    }

    [Theory]
    [InlineData("Development", "dotnet aspnetcore", "https://github.com/dotnet/aspnetcore")]
    [InlineData("Music", "Lofi hip hop radio", "https://www.youtube.com/watch?v=abc")]
    public void Classify_GeneralFoldersDoNotUseExternalProviders(string folderPath, string title, string url)
    {
        var result = BookmarkTagClassifier.Classify(title, url, folderPath, BookmarkTagDomainDto.Auto);

        Assert.Equal(BookmarkTagDomain.General, result.Domain);
        Assert.False(result.ShouldUseAniList);
        Assert.False(result.ShouldUseMangaUpdates);
    }

    [Fact]
    public void Classify_UserOverrideBeatsFolderHeuristic()
    {
        var result = BookmarkTagClassifier.Classify(
            "Some Chapter 1",
            "https://example.com/story",
            "General",
            BookmarkTagDomainDto.Novel);

        Assert.Equal(BookmarkTagDomain.Novel, result.Domain);
        Assert.True(result.ShouldUseMangaUpdates);
    }
}
