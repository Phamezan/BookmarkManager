using System;
using MudBlazor;

namespace BookmarkManager.Client.Extensions;

public static class SnackbarExtensions
{
    public static void AddApiError(this ISnackbar snackbar, string action, Exception ex)
    {
        snackbar.Add($"Failed to {action}: {ex.Message}", Severity.Error);
    }
}
