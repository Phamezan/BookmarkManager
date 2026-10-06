using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.Suwayomi;

namespace BookmarkManager.UnitTests.Discover;

public sealed class DiscoverTypeResolverTests
{
    [Theory]
    [InlineData("Manhwa")]
    [InlineData("manhwa")]
    [InlineData("WEBTOON")]
    public void WebtoonAndManhwa_MapToManhwa(string genre)
    {
        Assert.Equal(DiscoverSeriesType.Manhwa, DiscoverTypeResolver.Derive(["Action", genre]));
    }

    [Fact]
    public void Manhua_MapsToManhua() => Assert.Equal(DiscoverSeriesType.Manhua, DiscoverTypeResolver.Derive(["Manhua", "Action"]));

    [Fact]
    public void Manga_MapsToManga() => Assert.Equal(DiscoverSeriesType.Manga, DiscoverTypeResolver.Derive(["Manga"]));

    [Fact]
    public void FirstMatchingGenreWins() => Assert.Equal(DiscoverSeriesType.Manhua, DiscoverTypeResolver.Derive(["Action", "Manhua", "Manhwa"]));

    [Fact]
    public void NoMediaGenre_IsUnknown() => Assert.Equal(DiscoverSeriesType.Unknown, DiscoverTypeResolver.Derive(["Action", "Fantasy"]));

    [Fact]
    public void ToApiValue_ReturnsDisplayName()
    {
        Assert.Equal("Manhwa", DiscoverTypeResolver.ToApiValue(DiscoverSeriesType.Manhwa));
        Assert.Equal("Unknown", DiscoverTypeResolver.ToApiValue(DiscoverSeriesType.Unknown));
    }
}
