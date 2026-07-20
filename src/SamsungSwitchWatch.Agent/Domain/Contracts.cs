using System.Text.Json;
using System.Text.Json.Nodes;

namespace SamsungSwitchWatch.Agent.Domain;

public static class AgentErrorCodes
{
    public const string TcpTimeout = "TCP_TIMEOUT";
    public const string TelnetNegotiationFailed = "TELNET_NEGOTIATION_FAILED";
    public const string LoginPromptNotFound = "LOGIN_PROMPT_NOT_FOUND";
    public const string AuthFailed = "AUTH_FAILED";
    public const string CommandTimeout = "COMMAND_TIMEOUT";
    public const string PromptParseFailed = "PROMPT_PARSE_FAILED";
    public const string OutputLimitExceeded = "OUTPUT_LIMIT_EXCEEDED";
    public const string IncompleteOutput = "INCOMPLETE_OUTPUT";
    public const string ParserUnsupported = "PARSER_UNSUPPORTED";
    public const string Ipv6Unsupported = "IPV6_UNSUPPORTED";
    public const string TlsPinMismatch = "TLS_PIN_MISMATCH";
    public const string StorageWriteFailed = "STORAGE_WRITE_FAILED";
    public const string CommandNotAllowed = "COMMAND_NOT_ALLOWED";
    public const string DeviceNotFound = "DEVICE_NOT_FOUND";
    public const string PairingInvalid = "PAIRING_INVALID";
    public const string CredentialUnavailable = "CREDENTIAL_UNAVAILABLE";
    public const string CredentialCorrupt = "CREDENTIAL_CORRUPT";
    public const string CertificateUnavailable = "CERTIFICATE_UNAVAILABLE";
    public const string PairingRateLimited = "PAIRING_RATE_LIMITED";
    public const string CollectorInitializing = "COLLECTOR_INITIALIZING";
    public const string CollectorUnusable = "COLLECTOR_UNUSABLE";
}

public sealed class AgentOperationException(string code, string safeMessage, int statusCode = 400) : Exception(safeMessage)
{
    public string Code { get; } = code;
    public string SafeMessage { get; } = safeMessage;
    public int StatusCode { get; } = statusCode;
}

public enum EventState
{
    New,
    Acknowledged,
    Recovered
}

public enum EventSeverity
{
    Info,
    Warning,
    Critical,
    Recovery
}

public enum EventChangeKind
{
    Created,
    Acknowledged,
    Recovered
}

public sealed record StructuredEvent(
    long Sequence,
    string Id,
    string DeviceId,
    EventSeverity Severity,
    string Type,
    string Title,
    string Message,
    EventState State,
    DateTimeOffset OccurredUtc,
    DateTimeOffset? AcknowledgedUtc,
    DateTimeOffset? RecoveredUtc,
    string ConditionKey,
    IReadOnlyDictionary<string, string> Details,
    bool IsActiveCondition = false);

public sealed record NewEvent(
    string DeviceId,
    EventSeverity Severity,
    string Type,
    string Title,
    string Message,
    EventState State,
    string ConditionKey,
    IReadOnlyDictionary<string, string>? Details = null,
    DateTimeOffset? OccurredUtc = null,
    string? Id = null,
    bool IsActiveCondition = false);

public sealed record EventChange(
    long ChangeSequence,
    EventChangeKind ChangeKind,
    StructuredEvent Event);

public sealed record EventChangesPage(
    long HighWatermark,
    long NextCursor,
    bool HasMore,
    IReadOnlyList<EventChange> Changes,
    bool ResetRequired,
    long ResetCursor);

public sealed record ConditionRecoveryRequest(
    string DeviceId,
    string ConditionKey,
    DateTimeOffset RecoveredUtc,
    NewEvent RecoveryEvent);

public sealed record DeviceSnapshot(
    string DeviceId,
    string CommandId,
    DateTimeOffset CapturedUtc,
    JsonObject Data);

public sealed record CollectedOutput(
    string DeviceId,
    string CommandId,
    DateTimeOffset CapturedUtc,
    JsonObject Structured,
    string RawOutput,
    string CollectorStatus = "OK");

public sealed record CommandExecutionResult(
    string DeviceId,
    string CommandId,
    DateTimeOffset CapturedUtc,
    string CollectorStatus,
    JsonObject Structured,
    int EventsCreated);

public sealed record AuditEntry(
    DateTimeOffset OccurredUtc,
    string Action,
    string Actor,
    string? DeviceId,
    string Outcome,
    string Detail);

public sealed record CertificateStatus(bool HttpsEnabled, string? Sha256Fingerprint);

public sealed record EventSummary(long LastSequence, long ActiveCritical, long Unacknowledged);

public sealed record AgentReadiness(
    bool Ready,
    string Status,
    string? Code,
    int SchemaVersion,
    DateTimeOffset? SchedulerHeartbeatUtc,
    DateTimeOffset CheckedUtc);

public sealed class AgentRuntimeState
{
    private long _schedulerHeartbeatUtcTicks;

    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? SchedulerHeartbeatUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _schedulerHeartbeatUtcTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void TouchScheduler(DateTimeOffset at) =>
        Interlocked.Exchange(ref _schedulerHeartbeatUtcTicks, at.UtcTicks);
}

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Serializer = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };
}
