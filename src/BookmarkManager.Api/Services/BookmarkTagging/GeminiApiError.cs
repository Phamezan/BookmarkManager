using System.Net;
using System.Text;
using System.Text.Json;

namespace BookmarkManager.Api.Services.BookmarkTagging;

/// <summary>
/// Parses Google's standard Gemini error envelope
/// <c>{"error":{"code":..,"status":"..","message":".."}}</c> from a failed API response so a
/// diagnostic reason can carry Google's status token and a sanitized message instead of a bare HTTP
/// status code (e.g. a 402 with no explanation).
/// </summary>
/// <remarks>
/// Security: only <c>error.status</c> and <c>error.message</c> are ever surfaced. The API key, the
/// request URL/query, headers, and any other body content are never included. The message is
/// collapsed to a single line and length-capped, and the status token is restricted to
/// letters/digits/underscore.
/// </remarks>
internal static class GeminiApiError
{
    internal const int MaxMessageLength = 160;
    private const int MaxStatusLength = 64;

    /// <summary>
    /// Builds a safe reason such as <c>HTTP 402 PAYMENT_REQUIRED: &lt;message&gt;</c>. Falls back to
    /// <c>HTTP &lt;code&gt;</c> when the body is not the standard error JSON.
    /// </summary>
    public static string Describe(HttpStatusCode status, string? body)
    {
        var (statusToken, message) = Parse(body);
        var reason = new StringBuilder($"HTTP {(int)status}");
        if (!string.IsNullOrEmpty(statusToken))
        {
            reason.Append(' ').Append(statusToken);
        }

        if (!string.IsNullOrEmpty(message))
        {
            reason.Append(": ").Append(message);
        }

        return reason.ToString();
    }

    /// <summary>Extracts the sanitized <c>status</c>/<c>message</c> pair, or <c>(null, null)</c> when
    /// the body is not the standard error envelope.</summary>
    public static (string? Status, string? Message) Parse(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var status = error.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String
                ? SanitizeToken(statusElement.GetString())
                : null;
            var message = error.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String
                ? SanitizeMessage(messageElement.GetString())
                : null;
            return (status, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>Single-line, length-capped copy of Google's message. Control characters and runs of
    /// whitespace collapse to single spaces so the reason stays one log line.</summary>
    internal static string? SanitizeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var singleLine = CollapseWhitespace(message);
        if (singleLine.Length == 0)
        {
            return null;
        }

        return singleLine.Length > MaxMessageLength ? singleLine[..MaxMessageLength] : singleLine;
    }

    internal static string? SanitizeToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(token.Length, MaxStatusLength));
        foreach (var c in token)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                builder.Append(c);
            }

            if (builder.Length >= MaxStatusLength)
            {
                break;
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    private static string CollapseWhitespace(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (builder.Length > 0 && builder[^1] != ' ')
                {
                    builder.Append(' ');
                }

                continue;
            }

            builder.Append(c);
        }

        return builder.ToString().TrimEnd();
    }
}
