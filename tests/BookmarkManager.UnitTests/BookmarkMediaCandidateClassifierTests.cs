using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Contracts;

namespace BookmarkManager.UnitTests;

public sealed class BookmarkMediaCandidateClassifierTests
{
    [Theory]
    // NovelFull series pages encode the title in the path slug; CleanTitle prefers that
    // over the bookmark title (same as NovelFire — see MediaTitleNormalizer.Normalize).
    [InlineData("https://novelfull.com/a-monster-who-levels-up.html", "Novel Full")]
    public void Classify_NovelUrls_ReturnNovelDomainAndBypassAi(string url, string description)
    {
        _ = description;
        var classification = BookmarkMediaCandidateClassifier.Classify(
            "A Monster Who Levels Up Chapter 5",
            url,
            "General Folder");

        Assert.False(classification.RequiresAi);
        Assert.Equal(BookmarkTagDomain.Novel, classification.Domain);
        Assert.Equal("a monster who levels up", classification.CanonicalTitle);
        Assert.Contains("Matched Novel host", classification.Reason);
    }

    [Theory]
    [InlineData("Folder / Novels", "Novels")]
    [InlineData("Light Novel Folder", "Light Novel")]
    [InlineData("My Web Novel Collection", "Web Novel")]
    [InlineData("light novel", "lowercase")]
    public void Classify_NovelFolderPaths_ReturnNovelDomainAndBypassAi(string folderPath, string description)
    {
        _ = description;
        var classification = BookmarkMediaCandidateClassifier.Classify(
            "A Monster Who Levels Up - Chapter 12", 
            "https://example.com/some-link", 
            folderPath);

        Assert.False(classification.RequiresAi);
        Assert.Equal(BookmarkTagDomain.Novel, classification.Domain);
        Assert.Equal("A Monster Who Levels Up", classification.CanonicalTitle);
        Assert.Contains("Folder path contains Novel pattern", classification.Reason);
    }

    [Fact]
    public void Classify_MangaUrls_ReturnMangaDomainAndBypassAi()
    {
        var classification = BookmarkMediaCandidateClassifier.Classify(
            "Solo Leveling Chapter 45", 
            "https://www.mangaupdates.com/series.html?id=123", 
            "General Folder");

        Assert.False(classification.RequiresAi);
        Assert.Equal(BookmarkTagDomain.Manga, classification.Domain);
        Assert.Equal("Solo Leveling", classification.CanonicalTitle);
        Assert.Contains("Matched Manga host", classification.Reason);
    }

    [Theory]
    [InlineData("Manga Collection", "Manga")]
    [InlineData("Manhwa Scans", "Manhwa")]
    [InlineData("Manhua Releases", "Manhua")]
    [InlineData("manhwa", "lowercase")]
    public void Classify_MangaFolderPaths_ReturnMangaDomainAndBypassAi(string folderPath, string description)
    {
        _ = description;
        var classification = BookmarkMediaCandidateClassifier.Classify(
            "Solo Leveling - Vol 1 Chapter 2", 
            "https://example.com/some-link", 
            folderPath);

        Assert.False(classification.RequiresAi);
        Assert.Equal(BookmarkTagDomain.Manga, classification.Domain);
        Assert.Equal("Solo Leveling", classification.CanonicalTitle);
        Assert.Contains("Folder path contains Manga pattern", classification.Reason);
    }

    [Theory]
    [InlineData("Anime Subbed", "Anime")]
    [InlineData("anime", "lowercase")]
    public void Classify_AnimeFolderPaths_ReturnAnimeDomainAndBypassAi(string folderPath, string description)
    {
        _ = description;
        var classification = BookmarkMediaCandidateClassifier.Classify(
            "One Piece Episode 24", 
            "https://example.com/some-link", 
            folderPath);

        Assert.False(classification.RequiresAi);
        Assert.Equal(BookmarkTagDomain.Anime, classification.Domain);
        Assert.Equal("One Piece", classification.CanonicalTitle);
        Assert.Contains("Folder path contains Anime pattern", classification.Reason);
    }

    [Fact]
    public void Classify_AmbiguousCandidates_RequireAi()
    {
        var classification = BookmarkMediaCandidateClassifier.Classify(
            "Some Random Bookmark Title", 
            "https://example.com/random", 
            "General Folder");

        Assert.True(classification.RequiresAi);
        Assert.Equal(BookmarkTagDomain.General, classification.Domain);
        Assert.Equal("Some Random Bookmark Title", classification.CanonicalTitle);
        Assert.Equal("Requires AI identification", classification.Reason);
    }
}
