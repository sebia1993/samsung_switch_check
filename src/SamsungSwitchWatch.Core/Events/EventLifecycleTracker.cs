using SamsungSwitchWatch.Core.Diagnostics;

namespace SamsungSwitchWatch.Core.Events;

public sealed class EventLifecycleTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, MonitorEventDto> _activeByCondition = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, MonitorEventDto> _latestByEventId = [];
    private long _sequence;

    public EventLifecycleTracker(long lastSequence = 0, IEnumerable<MonitorEventDto>? activeEvents = null)
    {
        if (lastSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lastSequence));
        }

        _sequence = lastSequence;
        if (activeEvents is null)
        {
            return;
        }

        foreach (var item in activeEvents.Where(static item => item.Status is not MonitorEventStatus.Recovered))
        {
            _activeByCondition[item.ConditionKey] = item;
            _latestByEventId[item.EventId] = item;
            _sequence = Math.Max(_sequence, item.Sequence);
        }
    }

    public long LastSequence
    {
        get
        {
            lock (_gate)
            {
                return _sequence;
            }
        }
    }

    public IReadOnlyList<MonitorEventDto> ActiveEvents
    {
        get
        {
            lock (_gate)
            {
                return _activeByCondition.Values.OrderBy(static item => item.Sequence).ToArray();
            }
        }
    }

    public MonitorEventDto? OpenCondition(
        string deviceId,
        string conditionKey,
        MonitorEventKind kind,
        MonitorEventSeverity severity,
        string title,
        string message,
        DateTimeOffset occurredAt,
        string? previousValue = null,
        string? currentValue = null)
    {
        lock (_gate)
        {
            if (_activeByCondition.ContainsKey(conditionKey))
            {
                return null;
            }

            var created = Create(
                deviceId,
                conditionKey,
                kind,
                severity,
                MonitorEventStatus.New,
                title,
                message,
                occurredAt,
                previousValue,
                currentValue);
            _activeByCondition[conditionKey] = created;
            _latestByEventId[created.EventId] = created;
            return created;
        }
    }

    public MonitorEventDto EmitOneShot(
        string deviceId,
        string conditionKey,
        MonitorEventKind kind,
        MonitorEventSeverity severity,
        string title,
        string message,
        DateTimeOffset occurredAt,
        string? previousValue = null,
        string? currentValue = null)
    {
        lock (_gate)
        {
            var created = Create(
                deviceId,
                conditionKey,
                kind,
                severity,
                MonitorEventStatus.New,
                title,
                message,
                occurredAt,
                previousValue,
                currentValue);
            _latestByEventId[created.EventId] = created;
            return created;
        }
    }

    public MonitorEventDto? Acknowledge(Guid eventId, string acknowledgedBy, DateTimeOffset acknowledgedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acknowledgedBy);
        lock (_gate)
        {
            if (!_latestByEventId.TryGetValue(eventId, out var current) || current.Status is MonitorEventStatus.Recovered)
            {
                return null;
            }

            if (current.Status is MonitorEventStatus.Acknowledged)
            {
                return current;
            }

            var updated = current with
            {
                Sequence = NextSequence(),
                Status = MonitorEventStatus.Acknowledged,
                AcknowledgedAt = acknowledgedAt,
                AcknowledgedBy = DiagnosticRedactor.Redact(acknowledgedBy)
            };
            _latestByEventId[eventId] = updated;
            if (_activeByCondition.ContainsKey(updated.ConditionKey))
            {
                _activeByCondition[updated.ConditionKey] = updated;
            }

            return updated;
        }
    }

    public MonitorEventDto? RecoverCondition(
        string conditionKey,
        DateTimeOffset recoveredAt,
        string message,
        string? previousValue = null,
        string? currentValue = null)
    {
        lock (_gate)
        {
            if (!_activeByCondition.Remove(conditionKey, out var current))
            {
                return null;
            }

            var recovered = current with
            {
                Sequence = NextSequence(),
                Status = MonitorEventStatus.Recovered,
                Message = message,
                PreviousValue = previousValue ?? current.CurrentValue,
                CurrentValue = currentValue,
                RecoveredAt = recoveredAt
            };
            _latestByEventId[recovered.EventId] = recovered;
            return recovered;
        }
    }

    private MonitorEventDto Create(
        string deviceId,
        string conditionKey,
        MonitorEventKind kind,
        MonitorEventSeverity severity,
        MonitorEventStatus status,
        string title,
        string message,
        DateTimeOffset occurredAt,
        string? previousValue,
        string? currentValue) =>
        new(
            Guid.NewGuid(),
            NextSequence(),
            deviceId,
            conditionKey,
            kind,
            severity,
            status,
            title,
            message,
            occurredAt,
            previousValue,
            currentValue);

    private long NextSequence() => checked(++_sequence);
}
