using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BookmarkManager.Client.Services;

public sealed record UndoAction(Guid Id, string Description, Func<Task> RevertAction);

public class UndoService
{
    private readonly List<UndoAction> _actions = new();
    private const int MaxActions = 20;

    public UndoAction Push(string description, Func<Task> revert)
    {
        var action = new UndoAction(Guid.NewGuid(), description, revert);
        _actions.Add(action);
        if (_actions.Count > MaxActions)
        {
            _actions.RemoveAt(0);
        }
        return action;
    }

    public async Task<bool> UndoAsync(Guid id)
    {
        var action = _actions.FirstOrDefault(a => a.Id == id);
        if (action == null) return false;

        _actions.Remove(action);
        await action.RevertAction();
        return true;
    }

    /// <summary>Reverts the most recently pushed action (global Ctrl+Z path).</summary>
    public async Task<bool> UndoLatestAsync()
    {
        if (_actions.Count == 0) return false;

        var action = _actions[^1];
        _actions.RemoveAt(_actions.Count - 1);
        await action.RevertAction();
        return true;
    }

    public int Count => _actions.Count;
}
