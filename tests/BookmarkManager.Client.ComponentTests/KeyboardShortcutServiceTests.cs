using BookmarkManager.Client.Services;

namespace BookmarkManager.Client.ComponentTests;

/// <summary>
/// Covers the .NET-side registry only. Input-field/dialog suppression lives in
/// <c>wwwroot/js/keyboard-shortcuts.js</c> (no dotnet call happens at all in that
/// case) and is not unit-testable from here — verified manually / via bunit DOM
/// assertions if ever needed.
/// </summary>
public sealed class KeyboardShortcutServiceTests
{
    private static KeyboardShortcutEventDto KeyEvent(string key, bool ctrl = false, bool shift = false, bool alt = false, bool meta = false) =>
        new() { Key = key, Code = key, CtrlKey = ctrl, ShiftKey = shift, AltKey = alt, MetaKey = meta };

    [Fact]
    public async Task Register_MatchingKeyDown_InvokesHandlerAndReturnsTrue()
    {
        var service = new KeyboardShortcutService();
        var invoked = false;
        service.Register("p", ctrl: true, shift: false, alt: false, KeyboardShortcutService.GlobalContext, () =>
        {
            invoked = true;
            return Task.FromResult(true);
        });

        var handled = await service.OnKeyDown(KeyEvent("p", ctrl: true));

        Assert.True(invoked);
        Assert.True(handled);
    }

    [Fact]
    public async Task OnKeyDown_NoMatchingRegistration_ReturnsFalse()
    {
        var service = new KeyboardShortcutService();
        service.Register("p", ctrl: true, shift: false, alt: false, KeyboardShortcutService.GlobalContext, () => Task.FromResult(true));

        var handled = await service.OnKeyDown(KeyEvent("z", ctrl: true));

        Assert.False(handled);
    }

    [Fact]
    public async Task MetaKey_IsTreatedEquivalentToCtrlKey()
    {
        var service = new KeyboardShortcutService();
        var invoked = false;
        service.Register("p", ctrl: true, shift: false, alt: false, KeyboardShortcutService.GlobalContext, () =>
        {
            invoked = true;
            return Task.FromResult(true);
        });

        var handled = await service.OnKeyDown(KeyEvent("p", meta: true));

        Assert.True(invoked);
        Assert.True(handled);
    }

    [Fact]
    public async Task ContextHandler_IgnoredWhenContextInactive()
    {
        var service = new KeyboardShortcutService();
        var invoked = false;
        service.Register("ArrowDown", ctrl: false, shift: false, alt: false, "bookmarks-list", () =>
        {
            invoked = true;
            return Task.FromResult(true);
        });

        var handled = await service.OnKeyDown(KeyEvent("ArrowDown"));

        Assert.False(invoked);
        Assert.False(handled);
    }

    [Fact]
    public async Task ContextHandler_InvokedWhenContextActive()
    {
        var service = new KeyboardShortcutService();
        var invoked = false;
        service.Register("ArrowDown", ctrl: false, shift: false, alt: false, "bookmarks-list", () =>
        {
            invoked = true;
            return Task.FromResult(true);
        });

        service.SetContextActive("bookmarks-list", true);
        var handled = await service.OnKeyDown(KeyEvent("ArrowDown"));

        Assert.True(invoked);
        Assert.True(handled);
    }

    [Fact]
    public async Task GlobalContext_HandlerAlwaysEligible_RegardlessOfActiveContexts()
    {
        var service = new KeyboardShortcutService();
        var invoked = false;
        service.Register("p", ctrl: true, shift: false, alt: false, KeyboardShortcutService.GlobalContext, () =>
        {
            invoked = true;
            return Task.FromResult(true);
        });

        var handled = await service.OnKeyDown(KeyEvent("p", ctrl: true));

        Assert.True(invoked);
        Assert.True(handled);
    }

    [Fact]
    public async Task Register_DuplicateCombo_LastRegistrationWins()
    {
        var service = new KeyboardShortcutService();
        var firstInvoked = false;
        var secondInvoked = false;

        service.Register("Delete", ctrl: false, shift: false, alt: false, "bookmarks-list", () =>
        {
            firstInvoked = true;
            return Task.FromResult(true);
        });
        service.Register("Delete", ctrl: false, shift: false, alt: false, "bookmarks-list", () =>
        {
            secondInvoked = true;
            return Task.FromResult(true);
        });

        service.SetContextActive("bookmarks-list", true);
        await service.OnKeyDown(KeyEvent("Delete"));

        Assert.False(firstInvoked);
        Assert.True(secondInvoked);
    }

    [Fact]
    public async Task DisposingRegistration_UnregistersHandler()
    {
        var service = new KeyboardShortcutService();
        var invoked = false;
        var registration = service.Register("e", ctrl: false, shift: false, alt: false, "bookmarks-list", () =>
        {
            invoked = true;
            return Task.FromResult(true);
        });
        service.SetContextActive("bookmarks-list", true);

        registration.Dispose();
        var handled = await service.OnKeyDown(KeyEvent("e"));

        Assert.False(invoked);
        Assert.False(handled);
    }

    [Fact]
    public async Task KeyMatching_IsCaseInsensitive()
    {
        var service = new KeyboardShortcutService();
        var invoked = false;
        service.Register("E", ctrl: false, shift: false, alt: false, "bookmarks-list", () =>
        {
            invoked = true;
            return Task.FromResult(true);
        });
        service.SetContextActive("bookmarks-list", true);

        var handled = await service.OnKeyDown(KeyEvent("e"));

        Assert.True(invoked);
        Assert.True(handled);
    }

    [Fact]
    public async Task DeactivatingContext_StopsHandlerFromFiring()
    {
        var service = new KeyboardShortcutService();
        var invoked = false;
        service.Register("Delete", ctrl: false, shift: false, alt: false, "bookmarks-list", () =>
        {
            invoked = true;
            return Task.FromResult(true);
        });

        service.SetContextActive("bookmarks-list", true);
        service.SetContextActive("bookmarks-list", false);
        var handled = await service.OnKeyDown(KeyEvent("Delete"));

        Assert.False(invoked);
        Assert.False(handled);
    }
}
