using BookmarkManager.Api.Services.Suwayomi;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BookmarkManager.UnitTests.Suwayomi;

public sealed class SuwayomiOptionsBindingTests
{
    [Fact]
    public void SourceOrder_FromConfig_IsNotDuplicated()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Suwayomi:SourceOrder:0"] = "Asura Scans",
                ["Suwayomi:SourceOrder:1"] = "MangaDex",
            })
            .Build();

        var options = config.GetSection(SuwayomiOptions.SectionName).Get<SuwayomiOptions>()!;

        Assert.Equal(["Asura Scans", "MangaDex"], options.SourceOrder);
    }
}
