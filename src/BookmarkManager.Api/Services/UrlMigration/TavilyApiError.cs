using System.Net;
using System.Text;
using System.Text.Json;

namespace BookmarkManager.Api.Services.UrlMigration;

/// <summary>
/// Maps a failed Tavily response to a normalized, safe failure reason. Tavily's error envelope is
/// <c>{"detail":{"error":"..."}}</c> (or a <c>detail</c> array for 422 validation errors); only a
/// single, whitespace-collapsed, length-capped <c>detail.error</c> string is ever surfaced, never
/// the request URL, headers, API key, or any other body content.
/// </summary>
/// <remarks>
/// Status semantics verified against https://docs.tavily.com/documentation/api-reference/endpoint/search
/// (fetched 2026-10-02): 400 invalid request, 401 missing/invalid key, 422 validation, 429 rate
/// limit (<c>Retry-After</c>), 432 "Key limit or Plan Limit exceeded", 433 "PayGo limit exceeded",
/// 500 server error. The authorization header is <c>Authorization: Bearer &lt;key&gt;</c>.
/// </remarks>
internal static class TavilyApiError
{
    internal const int MaxMessageLength = 160;

    /// <summary>
    /// Builds a safe reason such as <c>HTTP 401: invalid or unauthorized key</c>, appending a
    /// sanitized Tavily message when one was returned. Known plan/credit codes are mapped to a
    /// fixed phrase so a 432/433 never depends on parsing the body.
    /// </summary>
    public static string Describe(HttpStatusCode status, string? body)
    {
        var reason = new StringBuilder($"HTTP {(int)status}");
        var hint = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "invalid or unauthorized key",
            HttpStatusCode.TooManyRequests => "rate limit reached",
            (HttpStatusCode)432 => "credit limit reached",
            (HttpStatusCode)433 => "credit limit reached",
            _ => null
        };

        if (hint is not null)
        {
            reason.Append(": ").Append(hint);
        }

        var message = SanitizeMessage(ParseMessage(body));
        if (!string.IsNullOrEmpty(message))
        {
            reason.Append(" (").Append(message).Append(')');
        }

        return reason.ToString();
    }

    /// <summary>Extracts <c>detail.error</c> (the documented ApiError shape), or null.</summary>
    public static string? ParseMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("detail", out var detail)
                || detail.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return detail.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Single-line, length-capped copy so the reason stays one log line.</summary>
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
