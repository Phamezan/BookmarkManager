using Microsoft.AspNetCore.Mvc;

namespace BookmarkManager.Api.Infrastructure;

public static class ApiProblem
{
    public const string ValidationCode = "VALIDATION";
    public const string NotFoundCode = "NOT_FOUND";
    public const string AuthRequiredCode = "AUTH_REQUIRED";
    public const string ForbiddenCode = "AUTH_FORBIDDEN";
    public const string ConflictCode = "CONFLICT";
    public const string InternalCode = "INTERNAL";

    private const string ProblemJsonContentType = "application/problem+json";

    public static ProblemDetails Create(int status, string code, string title, string? detail = null)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail
        };
        problem.Extensions["code"] = code;
        return problem;
    }

    // Return directly from actions (e.g. `return ApiProblem.Result(...)`). It already carries the
    // status code and the RFC 7807 content type, so it must not be wrapped by BadRequest/Unauthorized
    // helpers — wrapping would nest the ObjectResult and drop the problem+json content type.
    public static ObjectResult Result(int status, string code, string title, string? detail = null)
        => new(Create(status, code, title, detail))
        {
            StatusCode = status,
            ContentTypes = { ProblemJsonContentType }
        };
}
