using BookmarkManager.Api.Services;
using Xunit;

namespace BookmarkManager.UnitTests;

public class RelatedSeriesMatcherTests
{
    [Theory]
    [InlineData("That Time I Got Reincarnated as a Slime - Season 1", "That Time I Got Reincarnated as a Slime - Season 2")]
    [InlineData("Frieren: Beyond Journey's End - Season 1", "Frieren: Beyond Journey's End (Season 2)")]
    [InlineData("Solo Leveling - Cour 1", "Solo Leveling - Cour 2")]
    [InlineData("Solo Leveling - Chapter 150", "Solo Leveling - Chapter 151")]
    [InlineData("Jujutsu Kaisen [Anime]", "Jujutsu Kaisen - Season 2 (TV)")]
    [InlineData("Omniscient Reader's Viewpoint - Chapter 100", "Omniscient Reader's Viewpoint - Volume 2")]
    public void Match_SequelsAndChapters_ReturnsMatch(string titleA, string titleB)
    {
        var result = RelatedSeriesMatcher.Match(titleA, titleB);

        Assert.True(result.IsMatch, $"Expected match between '{titleA}' and '{titleB}', got score {result.Score}");
        Assert.True(result.Score >= 0.80, $"Expected score >= 0.80, got {result.Score}");
    }

    [Theory]
    [InlineData("That Time I Got Reincarnated as a Slime - Season 1", "Reborn as a Vending Machine, I Now Wander the Dungeon - Season 2")]
    [InlineData("One Piece", "One Punch Man")]
    [InlineData("Attack on Titan", "Clash of the Titans")]
    [InlineData("Sword Art Online", "Log Horizon")]
    [InlineData("Bleach", "Black Clover")]
    [InlineData("Fullmetal Alchemist", "Fullmetal Alchemist: Brotherhood")]
    [InlineData("Fate/stay night", "Fate/Zero")]
    [InlineData("Naruto", "Boruto: Naruto Next Generations")]
    public void Match_DistinctSeriesAndSpinOffs_RejectsMatch(string titleA, string titleB)
    {
        var result = RelatedSeriesMatcher.Match(titleA, titleB);

        Assert.False(result.IsMatch, $"Expected NO match between '{titleA}' and '{titleB}', got score {result.Score}");
    }

    [Fact]
    public void Match_EmptyOrWhitespace_ReturnsNoMatch()
    {
        var result1 = RelatedSeriesMatcher.Match("", "Some Title");
        var result2 = RelatedSeriesMatcher.Match("   ", "   ");

        Assert.False(result1.IsMatch);
        Assert.False(result2.IsMatch);
    }
}
