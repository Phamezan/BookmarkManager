using BookmarkManager.Client.Components.CommandPalette;

namespace BookmarkManager.Client.ComponentTests;

public sealed class CommandPaletteManagerOriginTests
{
    private static readonly Uri ManagerBase = new("https://phamezan.capybara-pirarucu.ts.net:8443/");

    [Theory]
    [InlineData("https://phamezan.capybara-pirarucu.ts.net:8443/bookmarks")]
    [InlineData("http://phamezan.capybara-pirarucu.ts.net:8080/library")]
    [InlineData("https://PHAMEZAN.capybara-pirarucu.ts.net:8443/")]
    public void ManagerTab_IsManagerOrigin(string context)
        => Assert.True(CommandPalette.IsManagerOrigin(new Uri(context), ManagerBase));

    [Theory]
    [InlineData("http://phamezan.capybara-pirarucu.ts.net:4567/manga/7866/chapter/28")]
    [InlineData("https://example.com/manga/1")]
    [InlineData("http://phamezan.capybara-pirarucu.ts.net:5080/")]
    public void OtherAppOrSite_IsNotManagerOrigin(string context)
        => Assert.False(CommandPalette.IsManagerOrigin(new Uri(context), ManagerBase));

    [Fact]
    public void DevProfile_PairsHttpPortWithHttpsPort()
        => Assert.True(CommandPalette.IsManagerOrigin(
            new Uri("http://host.ts.net:5080/"), new Uri("https://host.ts.net:5443/")));
}
