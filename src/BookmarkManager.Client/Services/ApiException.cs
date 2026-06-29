using System.Net;

namespace BookmarkManager.Client.Services;

public class ApiException : Exception
{
    public ApiException(
        HttpStatusCode statusCode,
        string title,
        string? detail = null,
        string? code = null,
        IReadOnlyDictionary<string, string[]>? validationErrors = null)
        : base(detail ?? title)
    {
        StatusCode = statusCode;
        Title = title;
        Detail = detail;
        Code = code;
        ValidationErrors = validationErrors ?? new Dictionary<string, string[]>();
    }

    public HttpStatusCode StatusCode { get; }
    public string Title { get; }
    public string? Detail { get; }
    public string? Code { get; }
    public IReadOnlyDictionary<string, string[]> ValidationErrors { get; }
}

public sealed class ApiValidationException : ApiException
{
    public ApiValidationException(
        HttpStatusCode statusCode,
        string title,
        string? detail,
        string? code,
        IReadOnlyDictionary<string, string[]> validationErrors)
        : base(statusCode, title, detail, code, validationErrors)
    {
    }
}

public sealed class ApiAuthenticationException : ApiException
{
    public ApiAuthenticationException(HttpStatusCode statusCode, string title, string? detail, string? code)
        : base(statusCode, title, detail, code)
    {
    }
}

public sealed class ApiAuthorizationException : ApiException
{
    public ApiAuthorizationException(HttpStatusCode statusCode, string title, string? detail, string? code)
        : base(statusCode, title, detail, code)
    {
    }
}

public sealed class ApiConflictException : ApiException
{
    public ApiConflictException(HttpStatusCode statusCode, string title, string? detail, string? code)
        : base(statusCode, title, detail, code)
    {
    }
}

public sealed class ApiNetworkException : Exception
{
    public ApiNetworkException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
