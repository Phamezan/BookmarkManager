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
    // Streaming-site slugs carry the clean series title even when the page <title> is junk;
    // the trailing site id/hash is stripped, real numeric title tokens are kept.
    [InlineData("https://zorox.to/watch/mob-psycho-100-iii-yqqv0/ep-1", "mob psycho 100 iii")]
    [InlineData("https://9animetv.to/watch/pluto-15516?ep=108996", "pluto")]
    [InlineData("https://9animetv.to/watch/witch-hat-atelier-20578?ep=169840", "witch hat atelier")]
    [InlineData("https://aniwatchtv.to/watch/eighty-six-2nd-season-17760?ep=88228", "eighty six 2nd season")]
    [InlineData("https://www4.gogoanime.pro/anime/noblesse-540q/ep-3", "noblesse")]
    [InlineData("https://zorox.to/watch/fruits-basket-2019-kn86/ep-1", "fruits basket 2019")]
    public void TryTitleFromStreamingUrl_ExtractsCleanTitle(string url, string expected)
    {
        Assert.Equal(expected, MediaTitleNormalizer.TryTitleFromStreamingUrl(url));
    }

    [Theory]
    // Non-streaming hosts must not be slug-mined - their paths are not canonical titles.
    [InlineData("https://asurascans.com/solo-leveling-chapter-12")]
    [InlineData("https://example.com/some/random/page")]
    [InlineData("not-a-url")]
    public void TryTitleFromStreamingUrl_ReturnsNullForNonStreamingHosts(string url)
    {
        Assert.Null(MediaTitleNormalizer.TryTitleFromStreamingUrl(url));
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
        // "A Monster Who Levels Up" has 5 real title tokens, which now fits within the
        // default maxTokens (8), so nothing gets truncated.
        var query = MediaTitleNormalizer.BuildLooseQuery("A Monster Who Levels Up");

        Assert.Equal("a monster who levels up", query);
    }

    [Fact]
    public void BuildLooseQuery_HonorsMaxTokensOverride()
    {
        var query = MediaTitleNormalizer.BuildLooseQuery("A B C D E F G H I J", maxTokens: 8);

        Assert.Equal("a b c d e f g h", query);
    }

    [Theory]
    // NovelFire encodes the clean series slug in the URL path even when the page <title> is
    // long and noisy ("Series - Chapter N: chapter title - Novel Fire").
    [InlineData("https://novelfire.net/book/young-masters-pov-woke-up-as-a-villain-in-a-game-one-day/chapter-27", "young masters pov woke up as a villain in a game one day")]
    [InlineData("https://novelfire.net/book/death-game-starting-as-a-trickster-pretending-to-be-a-god/chapter-132", "death game starting as a trickster pretending to be a god")]
    [InlineData("https://novelfire.net/book/sleeping-to-immortality-getting-stronger-one-nap-at-a-time/chapter-80", "sleeping to immortality getting stronger one nap at a time")]
    [InlineData("https://novelfire.net/book/starting-as-a-son-in-law-to-establish-an-immortal-family", "starting as a son in law to establish an immortal family")]
    [InlineData("https://novelfull.com/martial-god-asura.html", "martial god asura")]
    public void TryTitleFromNovelSiteUrl_ExtractsSlug(string url, string expected)
    {
        Assert.Equal(expected, MediaTitleNormalizer.TryTitleFromNovelSiteUrl(url));
    }

    [Theory]
    [InlineData("https://example.com/not-a-novel/book/foo")]
    [InlineData("https://novelfire.net/search?keyword=foo")]
    [InlineData("https://novelfull.com/search.html")]
    [InlineData("https://novelfire.net/book/")]
    [InlineData("not-a-url")]
    public void TryTitleFromNovelSiteUrl_ReturnsNullForNonSeriesUrls(string url)
    {
        Assert.Null(MediaTitleNormalizer.TryTitleFromNovelSiteUrl(url));
    }

    [Theory]
    [InlineData(
        "Death Game: Starting as a Trickster, Pretending to Be a God - Chapter 132: The Psychological Society, Fluoxetine - Novel Fire",
        "https://novelfire.net/book/death-game-starting-as-a-trickster-pretending-to-be-a-god/chapter-132",
        BookmarkTagDomain.Novel,
        "death game starting as a trickster pretending to be a god")]
    [InlineData(
        "Sleeping to Immortality: Getting Stronger One Nap at a Time! - Chapter 80 \u2013 Something - Novel Fire",
        "https://novelfire.net/book/sleeping-to-immortality-getting-stronger-one-nap-at-a-time/chapter-80",
        BookmarkTagDomain.Novel,
        "sleeping to immortality getting stronger one nap at a time")]
    [InlineData(
        "Investing in the Reborn Empress, She Actually Calls Me 'Husband' - Chapter 100: Something - Novel Fire",
        "https://novelfire.net/book/investing-in-the-reborn-empress-she-actually-calls-me-husband/chapter-100",
        BookmarkTagDomain.Novel,
        "investing in the reborn empress she actually calls me husband")]
    public void Normalize_PrefersNovelFireUrlSlug(string title, string url, BookmarkTagDomain domain, string expectedQuery)
    {
        var result = MediaTitleNormalizer.Normalize(title, url, domain);

        Assert.NotEmpty(result.Candidates);
        Assert.Equal(expectedQuery, result.Candidates[0].Query);
        Assert.Equal("novel-site URL slug", result.Candidates[0].Reason);
    }

    [Fact]
    public void Normalize_ClassifiesNovelFireBrandSegment()
    {
        var result = MediaTitleNormalizer.Normalize(
            "Death Game - Chapter 132 - Novel Fire",
            "https://novelfire.net/book/death-game/chapter-132",
            BookmarkTagDomain.Novel);

        var brand = Assert.Single(result.Segments, segment => segment.Text == "Novel Fire");
        Assert.True(brand.Features.IsBrand);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://galaxytranslations97.com/transcendence-due-to-a-system-error/chapter-110")]
    [InlineData("https://lightnovels.me/transcendence-due-to-a-system-error/chapter-110")]
    [InlineData("https://example.com/x")]
    public void Normalize_TranscendenceGalaxyTranslations_RanksSeriesTitleFirst(string? url)
    {
        const string title = "Transcendence Due To A System Error - Chapter 110 - Galaxy Translations";
        var result = MediaTitleNormalizer.Normalize(title, url, BookmarkTagDomain.Novel);

        Assert.NotEmpty(result.Candidates);
        Assert.Equal("Transcendence Due To A System Error", result.Candidates[0].Query);

        var brand = Assert.Single(result.Segments, segment => segment.Text == "Galaxy Translations");
        Assert.True(brand.Features.IsBrand);
    }

    [Fact]
    public void ClassifySegment_DueToInTitle_IsNotBrand()
    {
        var features = MediaTitleNormalizer.ClassifySegment("Transcendence Due To A System Error", 0, null);

        Assert.False(features.IsBrand);
        Assert.True(features.LooksLikeTitle);
    }

    [Theory]
    [InlineData("That Time I Got Reincarnated as a Slime Season 3", "Season 3")]
    [InlineData("that time i got reincarnated as a slime season 3", "Season 3")]
    public void ExtractSeasonMarker_ReturnsSeasonForSeasonNKeyword(string title, string expected)
    {
        Assert.Equal(expected, MediaTitleNormalizer.ExtractSeasonMarker(title));
    }

    [Fact]
    public void ExtractSeasonMarker_ReturnsSeasonForOrdinalSeasonKeyword()
    {
        Assert.Equal("Season 3", MediaTitleNormalizer.ExtractSeasonMarker("Eighty Six 3rd Season"));
    }

    [Fact]
    public void ExtractSeasonMarker_ReturnsPartForPartKeyword()
    {
        Assert.Equal("Part 2", MediaTitleNormalizer.ExtractSeasonMarker("Mushoku Tensei II Part 2"));
    }

    [Fact]
    public void ExtractSeasonMarker_ReturnsNullWhenNoMarkerPresent()
    {
        Assert.Null(MediaTitleNormalizer.ExtractSeasonMarker("One Piece Episode 1092"));
    }

    [Fact]
    public void BuildLooseQuery_ReproTitle_KeepsSeasonNumberPastTokenCap()
    {
        var query = MediaTitleNormalizer.BuildLooseQuery(
            "That Time I Got Reincarnated as a Slime Season 3 English Sub/Dub online Free on Aniwatch.to");

        Assert.Contains("reincarnated", query);
        Assert.Contains("3", query.Split(' '));
    }

    [Fact]
    public void BuildLooseQuery_ShortCandidateWithoutSeasonMarker_IsUnaffectedByDefaultChange()
    {
        var query = MediaTitleNormalizer.BuildLooseQuery("Solo Leveling");

        Assert.Equal("solo leveling", query);
    }
}
