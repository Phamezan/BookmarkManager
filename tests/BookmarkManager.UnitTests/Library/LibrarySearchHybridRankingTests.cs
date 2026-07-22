using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Library;
using BookmarkManager.Contracts;
using Xunit;

namespace BookmarkManager.UnitTests.Library;

public sealed class LibrarySearchHybridRankingTests
{
    private static LibraryCatalogEntry Entry(string title, float[]? embedding = null, string? alternateTitles = null)
    {
        var entry = new LibraryCatalogEntry
        {
            Provider = "Novelfire",
            ProviderId = title,
            Title = title,
            AlternateTitles = alternateTitles,
            MediaType = LibraryMediaType.Webnovel,
            SourceUrl = "https://example.com"
        };
        if (embedding is not null)
            entry.SetEmbeddingVector(embedding);
        return entry;
    }

    [Fact]
    public void KeywordScore_RanksExactAbovePrefixAboveSubstring()
    {
        Assert.Equal(1.0, LibrarySearchService.KeywordScore("Solo Leveling", null, "Solo Leveling"));
        Assert.Equal(0.8, LibrarySearchService.KeywordScore("Solo Leveling Ragnarok", null, "Solo Leveling"));
        Assert.Equal(0.6, LibrarySearchService.KeywordScore("The Solo Leveling Story", null, "Solo Leveling"));
        Assert.Equal(0.4, LibrarySearchService.KeywordScore("Only I Level Up", "Solo Leveling", "Solo Leveling"));
    }

    [Fact]
    public void HybridScore_IgnoresSemanticBelowFloor()
    {
        // Orthogonal query/embedding => cosine 0 (< floor) => keyword-only score.
        var entry = Entry("Solo Leveling", embedding: [0f, 1f]);
        var score = LibrarySearchService.HybridScore(entry, "Solo Leveling", [1f, 0f]);

        Assert.Equal(0.6 * 1.0, score, precision: 5); // KeywordWeight * exact-match
    }

    [Fact]
    public void HybridScore_BlendsKeywordAndSemanticWhenAligned()
    {
        // Aligned unit vectors => cosine 1 (>= floor) => keyword + vector contributions both count.
        var entry = Entry("Solo Leveling", embedding: [1f, 0f]);
        var score = LibrarySearchService.HybridScore(entry, "Solo Leveling", [1f, 0f]);

        Assert.Equal((0.6 * 1.0) + (0.4 * 1.0), score, precision: 5);
    }

    [Fact]
    public void RankHybrid_PromotesSemanticallyCloserOfTwoEqualKeywordMatches()
    {
        // Both are substring matches (equal keyword score); only the embedding breaks the tie.
        var far = Entry("A shadow monarch tale", embedding: [0f, 1f]);
        var near = Entry("A shadow monarch saga", embedding: [1f, 0f]);

        var ranked = LibrarySearchService.RankHybrid([far, near], "shadow monarch", [1f, 0f]);

        Assert.Equal("A shadow monarch saga", ranked[0].Title);
    }

    [Fact]
    public void RankHybrid_KeepsKeywordOrderWhenNoEmbeddings()
    {
        var exact = Entry("Solo Leveling");
        var prefix = Entry("Solo Leveling Ragnarok");

        var ranked = LibrarySearchService.RankHybrid([prefix, exact], "Solo Leveling", [1f, 0f]);

        Assert.Equal("Solo Leveling", ranked[0].Title);
    }
}
