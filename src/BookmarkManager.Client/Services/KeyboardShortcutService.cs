using Microsoft.JSInterop;

namespace BookmarkManager.Client.Services;

/// <summary>
/// Central registry for the one global <c>keydown</c> listener installed by
/// <c>wwwroot/js/keyboard-shortcuts.js</c> (pattern mirrors the older
/// per-page listener in <c>command-palette.js</c>, now migrated to route
/// through this service — see <c>CommandPalette</c>'s Ctrl+P registration).
///
/// Handlers are scoped by a <c>context</c> string: <see cref="GlobalContext"/>
/// handlers are always eligible; any other context (e.g. <c>"bookmarks-list"</c>)
/// is only eligible while a page has marked it active via
/// <see cref="SetContextActive"/>. This lets multiple pages register the same
/// key combo (e.g. "Delete") without colliding, since only one page's context
/// is ever active at a time.
///
/// Registering the same (key, modifiers, context) combo twice is NOT an
/// error — the newer registration replaces the older one (last-register-wins),
/// so a page that re-initializes (e.g. after a hot navigation) doesn't end up
/// with duplicate handlers firing for the same keystroke.
/// </summary>
public sealed class KeyboardShortcutService : IAsyncDisposable
{
    public const string GlobalContext = "global";

    private sealed record Registration(string Key, bool Ctrl, bool Shift, bool Alt, string Context, Func<Task<bool>> Handler);

    private readonly List<Registration> _registrations = [];
    private readonly HashSet<string> _activeContexts = new(StringComparer.OrdinalIgnoreCase);

    private DotNetObjectReference<KeyboardShortcutService>? _dotNetRef;
    private bool _initialized;

    public void SetContextActive(string context, bool active)
    {
        if (active)
        {
            _activeContexts.Add(context);
        }
        else
        {
            _activeContexts.Remove(context);
        }
    }

    /// <summary>
    /// Registers a handler for a key combo within a context. Returns an
    /// <see cref="IDisposable"/> that unregisters it — callers should dispose
    /// this from their own Dispose/OnInitialized-paired cleanup (see
    /// <c>Bookmarks.Keyboard.cs</c>).
    /// </summary>
    public IDisposable Register(string key, bool ctrl, bool shift, bool alt, string context, Func<Task<bool>> handler)
    {
        _registrations.RemoveAll(r => Matches(r, key, ctrl, shift, alt, context));
        var registration = new Registration(key, ctrl, shift, alt, context, handler);
        _registrations.Add(registration);
        return new Unregistration(this, registration);
    }

    [JSInvokable]
    public async Task<bool> OnKeyDown(KeyboardShortcutEventDto e)
    {
        var ctrl = e.CtrlKey || e.MetaKey;

        // Snapshot: a handler may register/unregister shortcuts synchronously
        // (e.g. closing a dialog), which must not mutate the list mid-iteration.
        foreach (var registration in _registrations.ToArray())
        {
            if (!string.Equals(registration.Key, e.Key, StringComparison.OrdinalIgnoreCase))
                continue;
            if (registration.Ctrl != ctrl || registration.Shift != e.ShiftKey || registration.Alt != e.AltKey)
                continue;
            if (!IsEligible(registration.Context))
                continue;

            return await registration.Handler();
        }

        return false;
    }

    public async Task EnsureInitializedAsync(IJSRuntime js)
    {
        if (_initialized) return;
        _initialized = true;

        _dotNetRef = DotNetObjectReference.Create(this);
        await js.InvokeVoidAsync("initializeKeyboardShortcuts", _dotNetRef);
    }

    public async ValueTask DisposeAsync()
    {
        _dotNetRef?.Dispose();
        _dotNetRef = null;
        await Task.CompletedTask;
    }

    private bool IsEligible(string context) =>
        string.Equals(context, GlobalContext, StringComparison.OrdinalIgnoreCase) || _activeContexts.Contains(context);

    private static bool Matches(Registration r, string key, bool ctrl, bool shift, bool alt, string context) =>
        string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase)
        && r.Ctrl == ctrl && r.Shift == shift && r.Alt == alt
        && string.Equals(r.Context, context, StringComparison.OrdinalIgnoreCase);

    private sealed class Unregistration(KeyboardShortcutService owner, Registration registration) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner._registrations.Remove(registration);
        }
    }
}
