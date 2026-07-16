using BookmarkManager.Client.Services;

namespace BookmarkManager.Client.ComponentTests;

public sealed class UndoServiceTests
{
    [Fact]
    public async Task UndoLatestAsync_PopsMostRecentlyPushedAction()
    {
        var service = new UndoService();
        var order = new List<string>();

        service.Push("first", () => { order.Add("first"); return Task.CompletedTask; });
        service.Push("second", () => { order.Add("second"); return Task.CompletedTask; });
        service.Push("third", () => { order.Add("third"); return Task.CompletedTask; });

        var undone = await service.UndoLatestAsync();

        Assert.True(undone);
        Assert.Equal(["third"], order);
        Assert.Equal(2, service.Count);
    }

    [Fact]
    public async Task UndoLatestAsync_RepeatedCalls_PopInReverseOrder()
    {
        var service = new UndoService();
        var order = new List<string>();

        service.Push("first", () => { order.Add("first"); return Task.CompletedTask; });
        service.Push("second", () => { order.Add("second"); return Task.CompletedTask; });
        service.Push("third", () => { order.Add("third"); return Task.CompletedTask; });

        await service.UndoLatestAsync();
        await service.UndoLatestAsync();
        await service.UndoLatestAsync();

        Assert.Equal(["third", "second", "first"], order);
        Assert.Equal(0, service.Count);
    }

    [Fact]
    public async Task UndoLatestAsync_EmptyStack_ReturnsFalse()
    {
        var service = new UndoService();

        var undone = await service.UndoLatestAsync();

        Assert.False(undone);
    }

    [Fact]
    public async Task UndoLatestAsync_AfterExhaustingStack_ReturnsFalse()
    {
        var service = new UndoService();
        service.Push("only", () => Task.CompletedTask);

        await service.UndoLatestAsync();
        var secondAttempt = await service.UndoLatestAsync();

        Assert.False(secondAttempt);
    }

    [Fact]
    public void Push_BeyondMaxActions_EvictsOldestEntry()
    {
        var service = new UndoService();

        for (var i = 0; i < 25; i++)
        {
            service.Push($"action-{i}", () => Task.CompletedTask);
        }

        Assert.Equal(20, service.Count);
    }

    [Fact]
    public async Task UndoAsync_ById_RemovesThatActionOnly_LeavesRestOnStack()
    {
        var service = new UndoService();
        var invoked = false;

        service.Push("first", () => Task.CompletedTask);
        var target = service.Push("second", () => { invoked = true; return Task.CompletedTask; });
        service.Push("third", () => Task.CompletedTask);

        var undone = await service.UndoAsync(target.Id);

        Assert.True(undone);
        Assert.True(invoked);
        Assert.Equal(2, service.Count);
    }
}
