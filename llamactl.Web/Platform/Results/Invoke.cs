using Immediate.Validations.Shared;

namespace llamactl.Web.Platform.Results;

public static class Invoke
{
    public static async Task<Result<T>> Guarded<T>(Func<CancellationToken, ValueTask<Result<T>>> call, ILogger logger, CancellationToken cancellationToken = default)
    {
        try { return await call(cancellationToken); }
        catch (ValidationException exception)
        {
            return Result<T>.Validation(string.Join(" ", exception.Errors.Select(error => error.ErrorMessage)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled failure in Blazor handler invocation");
            return Result<T>.AgentError("The operation failed unexpectedly.");
        }
    }
}