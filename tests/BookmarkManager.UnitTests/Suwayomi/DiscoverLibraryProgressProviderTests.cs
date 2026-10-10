using System.Text.Json;
using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services.BookmarkTagging;
using BookmarkManager.Api.Services.Suwayomi;

namespace BookmarkManager.UnitTests.Suwayomi;

public sealed class DiscoverLibraryProgressProviderTests
{
    private static SeriesMetadata Metadata(string key, params string[] titles) => new()
    {
        Id = Guid.NewGuid(),
        NormalizedKey = key,
        Found = true,
        TitlesJson = JsonSerializer.Serialize(titles),
        LookedUpAt = DateTime.UtcNow
    };

    [Fact]
    public void AliasTitleResolvesToLibraryProgress()
    {
        const string libraryTitle = "The Divine Demon's Grand Ascension";
        var library = new List<SuwayomiLibraryManga> { new(libraryTitle, 65, 68, 4360) };
        var metadata = new Dictionary<string, SeriesMetadata>(StringComparer.Ordinal)
        {
            [MediaTitleNormalizer.NormalizeForSearch(libraryTitle)] =
                Metadata("x", "Heavenly Demon Sovereign", libraryTitle)
        };

        var index = DiscoverLibraryProgressProvider.BuildIndex(library, metadata);

        var aliasKey = MediaTitleNormalizer.NormalizeForSearch("Heavenly Demon Sovereign");
        Assert.True(index.ContainsKey(aliasKey));
        Assert.Equal(4360, index[aliasKey].MangaId);
        Assert.Equal(65, index[aliasKey].Read);
        Assert.Equal(68, index[aliasKey].Latest);
    }

    [Fact]
    public void ExactTitleWinsOverAliasOnCollision()
    {
        var library = new List<SuwayomiLibraryManga>
        {
            new("Alpha", 10, 20, 100),
            new("Beta", 30, 40, 200)
        };
        var metadata = new Dictionary<string, SeriesMetadata>(StringComparer.Ordinal)
        {
            [MediaTitleNormalizer.NormalizeForSearch("Beta")] = Metadata("y", "Alpha")
        };

        var index = DiscoverLibraryProgressProvider.BuildIndex(library, metadata);

        var key = MediaTitleNormalizer.NormalizeForSearch("Alpha");
        Assert.Equal(100, index[key].MangaId);
        Assert.Equal(10, index[key].Read);
    }

    [Fact]
    public void MissMetadataAddsNoAliases()
    {
        var library = new List<SuwayomiLibraryManga> { new("Known", 1, 2, 7) };
        var metadata = new Dictionary<string, SeriesMetadata>(StringComparer.Ordinal)
        {
            [MediaTitleNormalizer.NormalizeForSearch("Known")] = new SeriesMetadata
            {
                Id = Guid.NewGuid(),
                NormalizedKey = "known",
                Found = false,
                TitlesJson = JsonSerializer.Serialize(new[] { "Some Other Name" }),
                LookedUpAt = DateTime.UtcNow
            }
        };

        var index = DiscoverLibraryProgressProvider.BuildIndex(library, metadata);

        Assert.True(index.ContainsKey(MediaTitleNormalizer.NormalizeForSearch("Known")));
        Assert.False(index.ContainsKey(MediaTitleNormalizer.NormalizeForSearch("Some Other Name")));
    }
}
