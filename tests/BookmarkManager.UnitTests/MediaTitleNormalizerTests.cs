using BookmarkManager.Api.Services.BookmarkTagging;

namespace BookmarkManager.UnitTests;

public sealed class MediaTitleNormalizerTests
{
    [Theory]
    [InlineData("Lightnovels.me - read A Monster Who Levels Up Chapter 48 online for free - No Pop-Ads", "https://lightnovels.me/some-page", BookmarkTagDomain.Novel, "A Monster Who Levels Up")]
    [InlineData("Martial God Asura Chapter 411 - Blood-Coloured Forbidden Medicine - Read Light Novels", "https://example.com/martial-god-asura-chapter-411", BookmarkTagDomain.Novel, "Martial God Asura")]
    [InlineData("Solo Leveling - Chapter 12 - Asura Scans", "https://asurascans.com/solo-leveling-chapter-12", BookmarkTagDomain.Manga, "Solo Leveling")]
    [InlineData("Watch MARRIAGETOXIN · Miruro - Episode 13", "https://miruro.tv/watch/marriagetoxin-episode-13", BookmarkTagDomain.Anime, "MARRIAGETOXIN")]
    [InlineData("Naruto Shippuden Episode 42 English Subbed - AnimePahe", "https://animepahe.ru/anime/naruto-shippuden", BookmarkTagDomain.Anime, "Naruto Shippuden")]
    public void Normalize_RanksExpectedTitleFirst(string title, string url, BookmarkTagDomain domain, string expected)
    {
        var result = MediaTitleNormalizer.Normalize(title, url, domain);

        Assert.NotEmpty(result.Candidates);
        Assert.Equal(expected, result.Candidates[0].Query);
    }

    [Theory]
    [InlineData("Re:Zero")]
    [InlineData("Sword Art Online: Alicization")]
    [InlineData("Fate/stay night: Unlimited Blade Works")]
    [InlineData("86")]
    [InlineData("Bleach")]
    [InlineData("Monster")]
    [InlineData("Kingdom")]
    public void Normalize_DoesNotBreakValidTitles(string title)
    {
        var result = MediaTitleNormalizer.Normalize(title, "https://example.com/title", BookmarkTagDomain.Anime);

        Assert.Equal(title, result.Candidates[0].Query);
    }

    [Theory]
    [InlineData("A Soldier's Life - Chapter 2: Training | Light Novel Pub", "https://www.lightnovelpub.com/soldiers-life", BookmarkTagDomain.Novel, "A Soldier's Life")]
    [InlineData("An Extra's POV - Chapter 436: Facing The Serocis [Pt 3] | Light Novel Pub", "https://www.lightnovelpub.com/extras-pov", BookmarkTagDomain.Novel, "An Extra's POV")]
    [InlineData("That Time I Got Reincarnated as a Slime - Chapter 50", "https://mangadex.org/title/slime", BookmarkTagDomain.Manga, "That Time I Got Reincarnated as a Slime")]
    public void Normalize_HandlesApostrophesInTitle(string title, string url, BookmarkTagDomain domain, string expected)
    {
        var result = MediaTitleNormalizer.Normalize(title, url, domain);
        Assert.NotEmpty(result.Candidates);
        Assert.Equal(expected, result.Candidates[0].Query);
    }

    [Theory]
    [InlineData("Soldier's Life", "soldiers life")]
    [InlineData("An Extra's POV", "an extras pov")]
    [InlineData("Re:Zero", "re zero")]
    public void NormalizeForSearch_RemovesApostrophesBeforeTokenizing(string input, string expected)
    {
        var result = MediaTitleNormalizer.NormalizeForSearch(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void BuildLooseQuery_DoesNotProduceApostropheSplitTokens()
    {
        var query = MediaTitleNormalizer.BuildLooseQuery("Soldier's Life");
        Assert.Equal("soldiers life", query);
    }

    [Fact]
    public void Normalize_UsesHostForBrandDetection()
    {
        var result = MediaTitleNormalizer.Normalize("Solo Leveling - Chapter 12 - Asura Scans", "https://asurascans.com/solo-leveling-chapter-12", BookmarkTagDomain.Manga);

        var brand = Assert.Single(result.Segments, segment => segment.Text == "Asura Scans");
        Assert.True(brand.Features.IsBrand);
    }

    [Fact]
    public void ClassifySegment_SeparatesChapterMarkersFromTitles()
    {
        var title = MediaTitleNormalizer.ClassifySegment("Martial God Asura", 0, null);
        var chapter = MediaTitleNormalizer.ClassifySegment("Chapter 411", 1, null);

        Assert.True(title.LooksLikeTitle);
        Assert.True(chapter.IsPureChapterMarker);
    }

    [Fact]
    public void BuildLooseQuery_UsesFirstMeaningfulWords()
    {
        var query = MediaTitleNormalizer.BuildLooseQuery("A Monster Who Levels Up");

        Assert.Equal("a monster who levels", query);
    }
}
