namespace SamsungSwitchWatch.Core.Events;

public enum MonitorEventSeverity
{
    Information,
    Warning,
    Critical
}

public enum MonitorEventStatus
{
    New,
    Acknowledged,
    Recovered
}

public enum MonitorEventKind
{
    NewLog,
    LogBufferReset,
    DeviceRebooted,
    InterfaceLink
}

/// <summary>
/// Sanitized event contract safe to expose through the Agent API. Sequence is
/// monotonic for every lifecycle update; EventId remains stable through
/// New/Acknowledged/Recovered transitions.
/// </summary>
public sealed record MonitorEventDto(
    Guid EventId,
    long Sequence,
    string DeviceId,
    string ConditionKey,
    MonitorEventKind Kind,
    MonitorEventSeverity Severity,
    MonitorEventStatus Status,
    string Title,
    string Message,
    DateTimeOffset OccurredAt,
    string? PreviousValue = null,
    string? CurrentValue = null,
    DateTimeOffset? AcknowledgedAt = null,
    string? AcknowledgedBy = null,
    DateTimeOffset? RecoveredAt = null);
