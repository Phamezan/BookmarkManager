using BookmarkManager.Api.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BookmarkManager.Api.Controllers;

/// <summary>Blocks every action with a 503 ProblemDetails when <c>Library:Enabled</c> is false, so the
/// feature can be switched off entirely (its background workers are not even registered) without the
/// endpoints crashing on missing dependencies or hitting a half-wired catalog.</summary>
public abstract class LibraryFeatureControllerBase : ControllerBase, IActionFilter
{
    private const string DisabledCode = "LIBRARY_DISABLED";

    protected abstract bool LibraryEnabled { get; }

    void IActionFilter.OnActionExecuting(ActionExecutingContext context)
    {
        if (!LibraryEnabled)
        {
            context.Result = ApiProblem.Result(
                StatusCodes.Status503ServiceUnavailable,
                DisabledCode,
                "Library is disabled",
                "The Library feature is turned off. Set Library:Enabled to true to use it.");
        }
    }

    void IActionFilter.OnActionExecuted(ActionExecutedContext context)
    {
    }
}
