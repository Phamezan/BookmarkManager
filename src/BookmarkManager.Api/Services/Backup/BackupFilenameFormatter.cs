using System.Text;
using BookmarkManager.Api.Data;

namespace BookmarkManager.Api.Services.Backup;

public static class BackupFilenameFormatter
{
    public static string Create(DateTimeOffset timestamp, string trigger)
    {
        var triggerSlug = SanitizeTrigger(trigger);
        var unique = Guid.NewGuid().ToString("N")[..8];
        return $"bookmarks-{timestamp:yyyy-MM-dd}-{timestamp:HHmmss}-{triggerSlug}-{unique}.db";
    }

    private static string SanitizeTrigger(string trigger)
    {
        if (string.IsNullOrWhiteSpace(trigger))
        {
            return "manual";
        }

        var builder = new StringBuilder(trigger.Length);
        foreach (var ch in trigger.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.Length > 0 ? builder.ToString() : "manual";
    }
}
