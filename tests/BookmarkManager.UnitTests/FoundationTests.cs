using BookmarkManager.Contracts;

namespace BookmarkManager.UnitTests;

public sealed class FoundationTests
{
    [Fact]
    public void ContractsAssemblyCanBeLoaded()
    {
        Assert.Equal("BookmarkManager.Contracts", typeof(AssemblyMarker).Assembly.GetName().Name);
    }
}
