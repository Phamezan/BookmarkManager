using BookmarkManager.Api.Services;
using Xunit;

namespace BookmarkManager.UnitTests;

public sealed class AnilistTaggingTests
{
    [Theory]
    [InlineData("One Piece - Episode 1092", "One Piece")]
    [InlineData("Jujutsu Kaisen - Chapter 245", "Jujutsu Kaisen")]
    [InlineData("[Season 3] Ep. 05 - The Return (5) | Kubera", "The Return")]
    [InlineData("990k Ex-Life Hunter - Chapter 41 - WEBTOON XYZ", "990k Ex-Life Hunter")]
    [InlineData("TFT ACADEMY | Home", "TFT ACADEMY")]
    [InlineData("Phamezan#NA1 - Set 16 Overview - LoLCHESS.GG", "Phamezan#NA1")]
    [InlineData("TFT Handbook - Robinsongz TFT Handbook", "TFT Handbook")]
    [InlineData("tft champ pool", "tft champ pool")]
    [InlineData("One Piece 1092 discussion", "One Piece 1092 discussion")]
    [InlineData("Watch MARRIAGETOXIN · Miruro - Episode 13", "MARRIAGETOXIN")]
    [InlineData("Watch Naruto · Crunchyroll - Episode 1", "Naruto")]
    public void CleanTitleForSearch_StripsCommonJunkCorrectly(string title, string expected)
    {
        var cleaned = AnilistTaggingService.CleanTitleForSearch(title);
        Assert.Equal(expected, cleaned);
    }
}
