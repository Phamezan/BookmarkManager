using BookmarkManager.Api.Services.Novels;
using Xunit;

namespace BookmarkManager.UnitTests.Novels;

public class NovelPathValidatorTests
{
    [Theory]
    [InlineData("/novel/my-story")]
    [InlineData("/novel/my-story/chapter-1")]
    [InlineData("/series/demo/vol-1/ch-2")]
    public void ValidatePath_AcceptsValidRelativePaths(string path)
    {
        Assert.Null(NovelPathValidator.ValidatePath(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("novel/no-leading-slash")]
    [InlineData("//evil.example/novel")]
    [InlineData("/novel//chapter-1")]
    [InlineData("/https://evil.example/novel")]
    [InlineData("/novel\r\nInjected: header")]
    public void ValidatePath_RejectsInvalidPaths(string? path)
    {
        Assert.NotNull(NovelPathValidator.ValidatePath(path));
    }

    [Fact]
    public void ValidatePath_RejectsPathsOver512Chars()
    {
        Assert.Null(NovelPathValidator.ValidatePath("/" + new string('a', 511)));
        Assert.NotNull(NovelPathValidator.ValidatePath("/" + new string('a', 512)));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("https://cdn.example.com/cover.webp", true)]
    [InlineData("http://cdn.example.com/cover.jpg", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("/relative/cover.png", false)]
    public void ValidateOptionalHttpUrl_EnforcesHttpOrHttps(string? url, bool shouldBeValid)
    {
        var err = NovelPathValidator.ValidateOptionalHttpUrl(url);
        Assert.Equal(shouldBeValid, err is null);
    }
}
