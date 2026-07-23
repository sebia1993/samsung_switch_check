namespace SamsungSwitchWatch.Agent.Domain;

/// <summary>
/// Stable, sanitized Agent API error codes. Values may be shown by the Viewer
/// and must never contain endpoint, credential, command, or device output data.
/// </summary>
public static class AgentErrorCodes
{
    public const string InternalError = "AGENT_INTERNAL_ERROR";
    public const string ConfigurationInvalid = "CONFIG_INVALID";
    public const string RequestInvalid = "REQUEST_INVALID";
    public const string RequestTooLarge = "REQUEST_TOO_LARGE";
    public const string TargetNotAllowed = "TARGET_NOT_ALLOWED";
    public const string AgentBusy = "AGENT_BUSY";
    public const string TlsIdentityInvalid = "TLS_IDENTITY_INVALID";
    public const string TcpTimeout = "TCP_TIMEOUT";
    public const string TelnetNegotiationFailed = "TELNET_NEGOTIATION_FAILED";
    public const string LoginPromptNotFound = "LOGIN_PROMPT_NOT_FOUND";
    public const string AuthFailed = "AUTH_FAILED";
    public const string EnableFailed = "ENABLE_FAILED";
    public const string CommandTimeout = "COMMAND_TIMEOUT";
    public const string PromptParseFailed = "PROMPT_PARSE_FAILED";
    public const string TelnetSessionClosed = "TELNET_SESSION_CLOSED";
    public const string OutputLimitExceeded = "OUTPUT_LIMIT_EXCEEDED";
    public const string Ipv6Unsupported = "IPV6_UNSUPPORTED";
    public const string QueryCommandBlocked = "QUERY_COMMAND_BLOCKED";
    public const string QueryRateLimited = "QUERY_RATE_LIMITED";
}

public sealed class AgentOperationException(
    string code,
    string safeMessage,
    int statusCode = 400) : Exception(safeMessage)
{
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
    public int StatusCode { get; } = statusCode;
}
