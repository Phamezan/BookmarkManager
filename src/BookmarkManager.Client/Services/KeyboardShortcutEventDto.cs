namespace BookmarkManager.Client.Services;

/// <summary>
/// Payload shape sent from <c>wwwroot/js/keyboard-shortcuts.js</c> on every
/// non-suppressed <c>keydown</c>. Property names are PascalCase; the JSON
/// deserializer Blazor's JS interop uses is case-insensitive, so the JS side
/// sends plain camelCase (<c>key</c>, <c>ctrlKey</c>, ...) without any mapping.
/// </summary>
public sealed class KeyboardShortcutEventDto
{
    public string Key { get; set; } = string.Empty;
    public string Code { get; set; } = string.Empty;
    public bool CtrlKey { get; set; }
    public bool MetaKey { get; set; }
    public bool AltKey { get; set; }
    public bool ShiftKey { get; set; }
}
