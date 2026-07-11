using System;

namespace BookmarkManager.Client.Services;

public interface ICommandPaletteService
{
    bool IsOpen { get; }
    event Action? OnToggle;
    void Toggle();
    void Open();
    void Close();
}

public sealed class CommandPaletteService : ICommandPaletteService
{
    public bool IsOpen { get; private set; }

    public event Action? OnToggle;

    public void Toggle()
    {
        IsOpen = !IsOpen;
        OnToggle?.Invoke();
    }

    public void Open()
    {
        if (!IsOpen)
        {
            IsOpen = true;
            OnToggle?.Invoke();
        }
    }

    public void Close()
    {
        if (IsOpen)
        {
            IsOpen = false;
            OnToggle?.Invoke();
        }
    }
}
