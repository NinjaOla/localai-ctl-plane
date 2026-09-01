using Immediate.Validations.Shared;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace llamactl.Web.Platform.Results;

internal sealed class ValidationExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ValidationException validationException)
            return ValueTask.FromResult(false);
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        var errors = validationException.Errors.GroupBy(error => error.PropertyName)
            .ToDictionary(group => group.Key, group => group.Select(error => error.ErrorMessage).ToArray());
        return problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ValidationProblemDetails(errors)
            {
                Title = "Validation failed",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            },
        });
    }
}

internal sealed class BadRequestExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not BadHttpRequestException)
            return ValueTask.FromResult(false);
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        return problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Title = "The request could not be read",
                Status = StatusCodes.Status400BadRequest,
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
            },
        });
    }
}

internal sealed class FallbackExceptionHandler(IProblemDetailsService problemDetails, ILogger<FallbackExceptionHandler> logger) : IExceptionHandler
{
    public ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Unhandled HTTP request failure");
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        return problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Title = "An unexpected error occurred",
                Status = StatusCodes.Status500InternalServerError,
                Type = "https://tools.ietf.org/html/rfc9110#section-15.6.1",
            },
        });
    }
}