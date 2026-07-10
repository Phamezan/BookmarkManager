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
    [InlineData("Web Novels", "Mother of Learning", "https://example.com/mother-of-learning")]
    [InlineData("Novels", "Lord of the Mysteries", "https://example.com/lord-of-the-mysteries")]
    [InlineData("Wuxia Novels", "Reverend Insanity", "https://novelfull.com/novel-book/reverend-insanity")]
    [InlineData("LN", "Classroom of the Elite Volume 1", "https://example.com/classroom-of-the-elite")]
    [InlineData("WN", "Worm", "https://example.com/worm")]
    public void Classify_UsesNovelFolderContext(string folderPath, string title, string url)
    {
        var result = BookmarkTagClassifier.Classify(title, url, folderPath, BookmarkTagDomainDto.Auto);

        Assert.Equal(BookmarkTagDomain.Novel, result.Domain);
        Assert.False(result.ShouldUseAniList);
        Assert.True(result.ShouldUseMangaUpdates);
    }

    [Theory]
    [InlineData("Novelty")]
    [InlineData("renovelsomething")]
    public void Classify_DoesNotUseNovelSubstringAsFolderContext(string folderPath)
    {
        var result = BookmarkTagClassifier.Classify("Example", "https://example.com/story", folderPath, BookmarkTagDomainDto.Auto);

        Assert.Equal(BookmarkTagDomain.General, result.Domain);
        Assert.False(result.ShouldUseMangaUpdates);
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

    [Theory]
    [InlineData("Noveller", BookmarkTagDomainDto.Novel)]
    [InlineData("Novelty", BookmarkTagDomainDto.General)]
    [InlineData("renovelsomething", BookmarkTagDomainDto.General)]
    [InlineData("Light Novels", BookmarkTagDomainDto.Novel)]
    [InlineData("Web Novels", BookmarkTagDomainDto.Novel)]
    [InlineData("Novels", BookmarkTagDomainDto.Novel)]
    [InlineData("LN", BookmarkTagDomainDto.Novel)]
    [InlineData("WN", BookmarkTagDomainDto.Novel)]
    public void GuessDefaultDomainFromFolderTitle_UsesTokenAndPhraseMatching(string folderTitle, BookmarkTagDomainDto expected)
    {
        Assert.Equal(expected, BookmarkTagClassifier.GuessDefaultDomainFromFolderTitle(folderTitle));
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

    [Theory]
    [InlineData("Player Who Returned 10,000 Years Later Chapter 132 - Novel Cool", "https://www.novelcool.com/chapter/Player/9775214/")]
    [InlineData("I Fell into the Game with Instant Kill - Chapter 137", "https://galaxytranslations97.com/novel/i-fell-into-the-game/chapter-137/")]
    [InlineData("Reincarnated as an Energy with a System", "https://novelusb.com/novel-book/reincarnated-as-an-energy")]
    [InlineData("The Young Master in the Shadows - Chapter 45", "https://www.scribblehub.com/read/413997/chapter/422833/")]
    public void Classify_UsesNovelUrlOrTitleAsDomainContext(string title, string url)
    {
        var result = BookmarkTagClassifier.Classify(title, url, "General", BookmarkTagDomainDto.Auto);

        Assert.Equal(BookmarkTagDomain.Novel, result.Domain);
        Assert.True(result.ShouldUseMangaUpdates);
    }

    [Theory]
    [InlineData("Villain Hides His True Colors - Chapter 12 - Reaper Scans", "https://reaperscans.com/series/villain-hides-his-true-colors/chapter-12/", "Villain Hides His True Colors")]
    [InlineData("Player Who Returned 10,000 Years Later Chapter 132 - Novel Cool", "https://www.novelcool.com/chapter/Player/", "Player Who Returned 10,000 Years Later")]
    [InlineData("I Fell into the Game with Instant Kill - Chapter 137 - Galaxy Translations", "https://galaxytranslations97.com/novel/", "I Fell into the Game with Instant Kill")]
    [InlineData("The Young Master in the Shadows - Chapter 45 - Scribble Hub", "https://www.scribblehub.com/read/", "The Young Master in the Shadows")]
    [InlineData("Solo Leveling - Novelusb", "https://novelusb.com/", "Solo Leveling")]
    [InlineData("Lightnovels.me - read A Monster Who Levels Up Chapter 48 online for free - No Pop-Ads", "https://lightnovels.me/chapter-48", "A Monster Who Levels Up")]
    [InlineData("Martial God Asura Chapter 411 - Blood-Coloured Forbidden Medicine - Read Light Novels", "https://readlightnovels.net/martial-god-asura/chapter-411-blood-coloured-forbidden-medicine.html", "Martial God Asura")]
    public void CleanTitle_StripsMetadataAndSiteSuffixes(string title, string url, string expectedCleanedTitle)
    {
        var cleaned = BookmarkTagClassifier.CleanTitle(title, url);
        Assert.Equal(expectedCleanedTitle, cleaned);
    }
}
