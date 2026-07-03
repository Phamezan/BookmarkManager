using BookmarkManager.Api.Services;
using BookmarkManager.Api.Services.BookmarkTagging;

namespace BookmarkManager.UnitTests;

public sealed class TagExtractorTests
{
    private readonly TagExtractorService _svc = new();

    [Theory]
    [InlineData("One Piece - Episode 1092", "https://crunchyroll.com/watch/one-piece/1092", "Anime")]
    [InlineData("Jujutsu Kaisen - Chapter 245", "https://mangadex.org/title/jjk/245", "Manga")]
    [InlineData("dotnet/aspnetcore", "https://github.com/dotnet/aspnetcore", "Development")]
    [InlineData("Lofi hip hop radio", "https://www.youtube.com/watch?v=jfKfPfyJRdk", "Video")]
    [InlineData("One Piece 1092 discussion", "https://www.reddit.com/r/OnePiece/comments/abc/x", "Social")]
    [InlineData("CNN - Breaking News", "https://www.cnn.com", "News")]
    [InlineData("Amazon.com: USB-C cable", "https://www.amazon.com/dp/B0XXXXX", "Shopping")]
    [InlineData("Kubernetes overview", "https://en.wikipedia.org/wiki/Kubernetes", "Reference")]
    public void CategoryRules_AssignExpectedCategory(string title, string url, string expected)
    {
        var tags = _svc.ExtractTags(title, url);
        Assert.Contains(expected, tags, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitHub_Repo_SurfacesBrandAndOwner()
    {
        var tags = _svc.ExtractTags("dotnet/aspnetcore", "https://github.com/dotnet/aspnetcore");
        Assert.Contains("GitHub", tags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Dotnet", tags, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void YouTube_SurfacesYouTubeTag()
    {
        var tags = _svc.ExtractTags("Lofi beats", "https://www.youtube.com/watch?v=abc");
        Assert.Contains("YouTube", tags, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reddit_Subreddit_SurfacesSubredditTag()
    {
        var tags = _svc.ExtractTags("Discussion", "https://www.reddit.com/r/OnePiece/comments/abc/x");
        Assert.Contains("Reddit", tags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(tags, t => t.StartsWith("r/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EpisodeSuffix_IsStrippedFromTitleBeforeTokenizing()
    {
        // " - Episode 1092" must not bleed "Episode" into tags.
        var tags = _svc.ExtractTags("One Piece - Episode 1092", "https://example.com");
        Assert.DoesNotContain("Episode", tags, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReturnsAtMostFiveTags()
    {
        var tags = _svc.ExtractTags(
            "Super Long Title With Many Distinct Interesting Words Here Today",
            "https://example.com");
        Assert.True(tags.Count <= 5);
    }

    [Fact]
    public void EmptyTitle_DoesNotThrow()
    {
        var tags = _svc.ExtractTags("", "https://example.com");
        Assert.NotNull(tags);
    }

    [Fact]
    public void FoldersAndTags_AreCaseNormalized()
    {
        var tags = _svc.ExtractTags("typescript handbook", "https://typescriptlang.org");
        Assert.Contains(tags, t => string.Equals(t, "Typescript", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StopWords_AreExcluded()
    {
        var tags = _svc.ExtractTags("The Best Way To Read The Page Online Free", "https://example.com");
        // None of these generic words should survive as tags.
        foreach (var stop in new[] { "The", "Best", "Way", "Read", "Page", "Online", "Free" })
            Assert.DoesNotContain(stop, tags, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void CamelCaseTokens_GetHigherPriority()
    {
        var tags = _svc.ExtractTags("Blazor WebAssembly tutorial", "https://learn.microsoft.com/blazor");
        Assert.Contains(tags, t => string.Equals(t, "Blazor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoiseWords_AreExcluded()
    {
        var tags = _svc.ExtractTags("Watch Miruro Anime English Subbed Dubbed Zoro Aniwave", "https://miruro.tv/watch/show");
        
        var noise = new[] { "Miruro", "English", "Subbed", "Dubbed", "Zoro", "Aniwave" };
        foreach (var word in noise)
        {
            Assert.DoesNotContain(word, tags, StringComparer.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void DomainConstraint_PreventsCrossDomainFormattingTags()
    {
        var tags = _svc.ExtractTags(
            "Frieren Anime (manga adaptation description)", 
            "https://miruro.tv/watch/frieren", 
            BookmarkTagDomain.Anime);

        Assert.Contains("Anime", tags);
        Assert.DoesNotContain("Manga", tags);
    }

    [Fact]
    public void DynamicBrandExclusion_FiltersOutSiteNameAndDomainSegments()
    {
        var page = new PageMetadata { SiteName = "Kaido Anime Player" };
        var tags = _svc.ExtractTags(
            "Frieren at the Funeral - Watch on Kaido",
            "https://sub.kaido.to/watch/123",
            BookmarkTagDomain.Anime,
            page);

        Assert.DoesNotContain("Kaido", tags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Frieren", tags);
    }

    [Fact]
    public void DomainSpecific_SuppressesGeneralCategoryRules()
    {
        // Title contains keywords that would normally match "Development" (github), "Video" (youtube), "Shopping" (shop).
        // Since the domain is Anime, these general categories must be suppressed.
        var tags = _svc.ExtractTags(
            "Frieren github youtube shop", 
            "https://miruro.tv/watch/frieren", 
            BookmarkTagDomain.Anime);

        Assert.Contains("Anime", tags);
        Assert.DoesNotContain("Development", tags);
        Assert.DoesNotContain("Video", tags);
        Assert.DoesNotContain("Shopping", tags);
    }

    [Fact]
    public void MatchCategoryRules_UsesWordBoundaries()
    {
        // "chapters" contains "chapter" as a substring but shouldn't trigger the Manga category rule.
        // "codebooks" contains "code" as a substring but shouldn't trigger the Development category rule.
        var tags = _svc.ExtractTags(
            "Book of chapters and codebooks",
            "https://example.com/books",
            BookmarkTagDomain.General);

        Assert.DoesNotContain("Manga", tags);
        Assert.DoesNotContain("Development", tags);
    }

    [Fact]
    public void MatchCategoryRules_NovelSignalsTakePrecedenceOverChapterMangaSignal()
    {
        // A novel bookmark with "chapter" in the title should yield "Novel", not "Manga"
        var tags = _svc.ExtractTags(
            "Player Who Returned 10,000 Years Later Chapter 132 - Novel Cool",
            "https://www.novelcool.com/chapter/Player/9775214/",
            BookmarkTagDomain.General);

        Assert.Contains("Novel", tags);
        Assert.DoesNotContain("Manga", tags);
    }
}
