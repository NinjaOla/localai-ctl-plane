namespace llamactl.Web.Platform.Results;

public enum ErrorKind
{
    Conflict,
    NotFound,
    Validation,
    NodeUnreachable,
    AgentError,
    Failure
}

public sealed record Error(ErrorKind Kind, string Message);

public sealed record Result<T>
{
    private Result(T? value, Error? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }
    public Error? Error { get; }
    public bool IsSuccess => Error is null;

    public static Result<T> Success(T value) => new(value, null);
    public static Result<T> Conflict(string message) => new(default, new(ErrorKind.Conflict, message));
}