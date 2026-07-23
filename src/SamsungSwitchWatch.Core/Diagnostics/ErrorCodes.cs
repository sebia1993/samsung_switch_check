namespace SamsungSwitchWatch.Core.Diagnostics;

/// <summary>
/// Stable, non-sensitive failure identifiers shared by the Agent and Viewer.
/// Do not rename existing values: field diagnostics and alert rules persist them.
/// </summary>
public static class ErrorCodes
{
    public const string TcpTimeout = "TCP_TIMEOUT";
    public const string TelnetNegotiationFailed = "TELNET_NEGOTIATION_FAILED";
    public const string LoginPromptNotFound = "LOGIN_PROMPT_NOT_FOUND";
    public const string AuthFailed = "AUTH_FAILED";
    public const string EnableFailed = "ENABLE_FAILED";
    public const string CommandTimeout = "COMMAND_TIMEOUT";
    public const string PromptParseFailed = "PROMPT_PARSE_FAILED";
    public const string TelnetSessionClosed = "TELNET_SESSION_CLOSED";
    public const string OutputLimitExceeded = "OUTPUT_LIMIT_EXCEEDED";
    public const string ParserUnsupported = "PARSER_UNSUPPORTED";
    public const string IncompleteOutput = "INCOMPLETE_OUTPUT";
    public const string Ipv6Unsupported = "IPV6_UNSUPPORTED";
    public const string CredentialCorrupt = "CREDENTIAL_CORRUPT";
    public const string StorageCorrupt = "STORAGE_CORRUPT";
    public const string TlsPinMismatch = "TLS_PIN_MISMATCH";
    public const string StorageWriteFailed = "STORAGE_WRITE_FAILED";
    public const string CollectorUnusable = "COLLECTOR_UNUSABLE";
}
