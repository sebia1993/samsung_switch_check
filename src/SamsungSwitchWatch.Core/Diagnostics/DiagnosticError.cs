namespace SamsungSwitchWatch.Core.Diagnostics;

public sealed record DiagnosticError(
    string Code,
    string Stage,
    string Message,
    bool IsRetryable = false)
{
    /// <summary>
    /// Gets bounded, sanitized command-progress information when a Telnet
    /// command fails. Endpoint, credential, command and output values are
    /// deliberately excluded.
    /// </summary>
    public CommandFailureTelemetry? CommandTelemetry { get; init; }
}

public sealed record CommandFailureTelemetry(
    long ElapsedMs,
    bool ReceivedOutput,
    int PagerCount);

public class SwitchWatchException : Exception
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

    public static ParseResult<T> Failure(string code, string stage, string message, bool retryable = false) =>
        new(default, new DiagnosticError(code, stage, message, retryable));
}
