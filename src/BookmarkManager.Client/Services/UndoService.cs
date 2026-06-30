using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace BookmarkManager.Client.Services;

public record UndoAction(string Description, Func<Task> RevertAction);

public class UndoService
{
    private readonly Stack<UndoAction> _undoStack = new();

    public void Push(string description, Func<Task> revertAction)
    {
        _undoStack.Push(new UndoAction(description, revertAction));
    }

    public bool CanUndo => _undoStack.Count > 0;

    public string? GetNextActionDescription() => _undoStack.Count > 0 ? _undoStack.Peek().Description : null;

    public async Task UndoAsync()
    {
        if (_undoStack.Count == 0) return;
        var action = _undoStack.Pop();
        await action.RevertAction();
    }

    public void Clear()
    {
        _undoStack.Clear();
    }
}
