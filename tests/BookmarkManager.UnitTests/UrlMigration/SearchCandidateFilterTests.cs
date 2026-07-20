using System.Collections.Generic;
using BookmarkManager.Api.Services.UrlMigration;
using Xunit;

namespace BookmarkManager.UnitTests.UrlMigration;

public class SearchCandidateFilterTests
{
    [Fact]
    public void Filter_DropsDeadHostAndSubdomains()
    {
        var candidates = new List<SearchCandidate>
        {
            new("https://flamecomics.xyz/series/solo-leveling/chapter-112", null, null),
            new("https://www.flamecomics.xyz/series/solo-leveling/chapter-112", null, null),
            new("https://cdn.flamecomics.xyz/img.png", null, null),
            new("https://asuracomic.net/series/solo-leveling/chapter-112", null, null),
        };

        var result = SearchCandidateFilter.Filter(candidates, "flamecomics.xyz");

        var url = Assert.Single(result).Url;
        Assert.Equal("https://asuracomic.net/series/solo-leveling/chapter-112", url);
    }

    [Theory]
    [InlineData("https://www.reddit.com/r/manga/thread")]
    [InlineData("https://soloraising.fandom.com/wiki/Chapter_112")]
    [InlineData("https://en.wikipedia.org/wiki/Solo_Leveling")]
    [InlineData("https://youtube.com/watch?v=abc")]
    [InlineData("https://x.com/someuser")]
    [InlineData("https://www.facebook.com/somepage")]
    [InlineData("https://pinterest.com/pin/1")]
    [InlineData("https://discord.gg/invite")]
    public void Filter_DropsNoiseHosts(string url)
    {
        var candidates = new List<SearchCandidate> { new(url, null, null) };

        var result = SearchCandidateFilter.Filter(candidates, "flamecomics.xyz");

        Assert.Empty(result);
    }

    [Fact]
    public void Filter_DropsNonHttpSchemes()
    {
        var candidates = new List<SearchCandidate>
        {
            new("ftp://example.com/file", null, null),
            new("javascript:alert(1)", null, null),
            new("not a url", null, null),
        };

        var result = SearchCandidateFilter.Filter(candidates, "flamecomics.xyz");

        Assert.Empty(result);
    }

    [Fact]
    public void Filter_DeduplicatesAndCapsAtMaxResults()
    {
        var candidates = new List<SearchCandidate>
        {
            new("https://asuracomic.net/a", null, null),
            new("https://asuracomic.net/a", null, null),
            new("https://asuracomic.net/b", null, null),
            new("https://asuracomic.net/c", null, null),
            new("https://asuracomic.net/d", null, null),
            new("https://asuracomic.net/e", null, null),
            new("https://asuracomic.net/f", null, null),
        };

        var result = SearchCandidateFilter.Filter(candidates, "flamecomics.xyz", maxResults: 5);

        Assert.Equal(5, result.Count);
    }

    [Fact]
    public void Filter_DeduplicatesSchemeWwwTrailingSlashAndFragmentVariants()
    {
        var candidates = new List<SearchCandidate>
        {
            new("http://www.asuracomic.net/series/solo-leveling/chapter-112/", null, null),
            new("https://asuracomic.net/series/solo-leveling/chapter-112#top", null, null),
            new("https://Asuracomic.net/Series/solo-leveling/chapter-112", null, null),
        };

        var result = SearchCandidateFilter.Filter(candidates, "flamecomics.xyz");

        Assert.Single(result);
    }

    [Fact]
    public void Filter_KeepsDistinctQueryStrings()
    {
        var candidates = new List<SearchCandidate>
        {
            new("https://miruro.tv/watch/123/slug?ep=11", null, null),
            new("https://miruro.tv/watch/123/slug?ep=12", null, null),
        };

        var result = SearchCandidateFilter.Filter(candidates, "flamecomics.xyz");

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void Filter_KeepsLegitimateReaderHost()
    {
        var candidates = new List<SearchCandidate>
        {
            new("https://mangadex.org/title/abc/solo-leveling", "Solo Leveling", "chapter 112"),
        };

        var result = SearchCandidateFilter.Filter(candidates, "flamecomics.xyz");

        Assert.Single(result);
    }
}

public class UrlComparisonNormalizerTests
{
    [Theory]
    [InlineData("http://www.Example.com/path/", "https://example.com/path")]
    [InlineData("https://example.com/path#frag", "https://example.com/path")]
    [InlineData("https://example.com/Path", "https://example.com/path")]
    [InlineData("https://example.com/path?ep=11", "https://example.com/path?ep=11")]
    public void Normalize_CollapsesSchemeWwwSlashFragmentAndCase(string input, string expected)
    {
        Assert.Equal(expected, UrlComparisonNormalizer.Normalize(input));
    }
}
