using BookmarkManager.Client.Services;
using BookmarkManager.Contracts;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace BookmarkManager.Client.Features.Library.Components;

/// <summary>Floating glassmorphism chat drawer for the Library AI assistant. Owns its own transcript
/// state and posts to <c>api/library/chat</c> through <see cref="IBookmarkService"/>; the host page
/// only toggles <see cref="Open"/>.</summary>
public partial class LibraryChatDrawer : IAsyncDisposable
{
    private const int MaxHistoryTurns = 12;

    [Inject] private IBookmarkService BookmarkService { get; set; } = default!;
    [Inject] private IJSRuntime JsRuntime { get; set; } = default!;

    [Parameter] public bool Open { get; set; }
    [Parameter] public EventCallback<bool> OpenChanged { get; set; }

    private readonly List<ChatEntry> _messages = [];
    private string _input = string.Empty;
    private bool _sending;
    private string? _error;
    private ElementReference _logRef;
    private CancellationTokenSource? _cts;

    private bool CanSend => !_sending && !string.IsNullOrWhiteSpace(_input);

    private void OnInput(ChangeEventArgs args) => _input = args.Value?.ToString() ?? string.Empty;

    private async Task OnKeyDown(KeyboardEventArgs args)
    {
        // Enter sends; Shift+Enter inserts a newline (native textarea behavior).
        if (args.Key == "Enter" && !args.ShiftKey)
        {
            await SendAsync();
        }
    }

    private async Task ToggleOpen()
    {
        Open = !Open;
        if (OpenChanged.HasDelegate)
            await OpenChanged.InvokeAsync(Open);
        if (Open)
            await ScrollToBottomAsync();
    }

    private async Task CloseAsync()
    {
        if (!Open)
            return;

        Open = false;
        if (OpenChanged.HasDelegate)
            await OpenChanged.InvokeAsync(false);
    }

    private async Task SendAsync()
    {
        if (!CanSend)
            return;

        var text = _input.Trim();
        _input = string.Empty;
        _error = null;

        var history = BuildHistory();
        _messages.Add(new ChatEntry("user", text));
        var pending = new ChatEntry("assistant", string.Empty) { IsPending = true };
        _messages.Add(pending);
        _sending = true;
        StateHasChanged();
        await ScrollToBottomAsync();

        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        try
        {
            var response = await BookmarkService.LibraryChatAsync(
                new LibraryChatRequestDto(text, history), _cts.Token);

            pending.Content = string.IsNullOrWhiteSpace(response.Markdown)
                ? "No response."
                : response.Markdown;
            pending.Recommendations = response.Series;
        }
        catch (OperationCanceledException)
        {
            _messages.Remove(pending);
        }
        catch (Exception ex)
        {
            _messages.Remove(pending);
            _error = $"Assistant unavailable: {ex.Message}";
        }
        finally
        {
            pending.IsPending = false;
            _sending = false;
            StateHasChanged();
            await ScrollToBottomAsync();
        }
    }

    /// <summary>Prior completed turns (excludes the message just typed), newest-trimmed to a bounded
    /// window so the request body stays small.</summary>
    private IReadOnlyList<ChatMessageDto> BuildHistory() =>
        _messages
            .Where(m => !m.IsPending && !string.IsNullOrEmpty(m.Content))
            .TakeLast(MaxHistoryTurns)
            .Select(m => new ChatMessageDto(m.Role, m.Content))
            .ToList();

    private async Task ScrollToBottomAsync()
    {
        try
        {
            await JsRuntime.InvokeVoidAsync("libraryChatScrollToBottom", _logRef);
        }
        catch
        {
            // Interop unavailable (prerender / tests) — non-fatal.
        }
    }

    public ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        return ValueTask.CompletedTask;
    }

    private sealed class ChatEntry(string role, string content)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public string Role { get; } = role;
        public string Content { get; set; } = content;
        public bool IsPending { get; set; }
        public IReadOnlyList<LibraryRecommendedSeriesDto>? Recommendations { get; set; }
    }
}
