using BookmarkManager.Api.Data;
using BookmarkManager.Api.Services;
using BookmarkManager.Contracts;
using Xunit;

namespace BookmarkManager.UnitTests;

public sealed class DeferredCommandHelperTests
{
    [Fact]
    public void InitialStatus_ReturnsPending_WhenParentIsNull()
    {
        Assert.Equal("Pending", DeferredCommandHelper.InitialStatus(null));
    }

    [Fact]
    public void InitialStatus_ReturnsPending_WhenParentIsConfirmed()
    {
        var parent = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Type = NodeType.Folder,
            Title = "Confirmed",
            BrowserNodeId = "42"
        };

        Assert.Equal("Pending", DeferredCommandHelper.InitialStatus(parent));
    }

    [Fact]
    public void InitialStatus_ReturnsDeferred_WhenParentHasNoBrowserNodeId()
    {
        var parent = new BookmarkNode
        {
            Id = Guid.NewGuid(),
            Type = NodeType.Folder,
            Title = "Unconfirmed",
            BrowserNodeId = null
        };

        Assert.Equal("Deferred", DeferredCommandHelper.InitialStatus(parent));
    }

    [Fact]
    public void RewriteParentBrowserNodeId_ReplacesTopLevelProperty()
    {
        var payload = """{"parentBrowserNodeId":"0","position":3}""";

        var rewritten = DeferredCommandHelper.RewriteParentBrowserNodeId(payload, "77");

        Assert.NotNull(rewritten);
        Assert.Contains("\"parentBrowserNodeId\":\"77\"", rewritten);
        Assert.Contains("\"position\":3", rewritten);
    }

    [Fact]
    public void RewriteParentBrowserNodeId_ReplacesNullPlaceholder()
    {
        var payload = """{"parentBrowserNodeId":null,"position":0}""";

        var rewritten = DeferredCommandHelper.RewriteParentBrowserNodeId(payload, "77");

        Assert.Contains("\"parentBrowserNodeId\":\"77\"", rewritten);
    }

    [Fact]
    public void RewriteParentBrowserNodeId_LeavesPayloadWithoutPropertyUntouched()
    {
        var payload = """{"recursive":true}""";

        var rewritten = DeferredCommandHelper.RewriteParentBrowserNodeId(payload, "77");

        Assert.Equal(payload, rewritten);
    }

    [Fact]
    public void RewriteParentBrowserNodeId_ReturnsInputOnNullOrInvalidJson()
    {
        Assert.Null(DeferredCommandHelper.RewriteParentBrowserNodeId(null, "77"));
        Assert.Equal("not-json", DeferredCommandHelper.RewriteParentBrowserNodeId("not-json", "77"));
    }
}
