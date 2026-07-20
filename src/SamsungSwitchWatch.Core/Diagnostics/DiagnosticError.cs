namespace SamsungSwitchWatch.Core.Diagnostics;

public sealed record DiagnosticError(
    string Code,
    string Stage,
    string Message,
    bool IsRetryable = false);

public sealed class SwitchWatchException : Exception
{
    public SwitchWatchException(DiagnosticError error, Exception? innerException = null)
        : base(error.Message, innerException)
    {
        Error = error;
    }

    public DiagnosticError Error { get; }
}

public sealed record ParseResult<T>(T? Value, DiagnosticError? Error)
{
    public bool IsSuccess => Error is null && Value is not null;

    public static ParseResult<T> Success(T value) => new(value, null);

    public static ParseResult<T> Unsupported(string stage, string message) =>
        new(default, new DiagnosticError(ErrorCodes.ParserUnsupported, stage, message));
}
