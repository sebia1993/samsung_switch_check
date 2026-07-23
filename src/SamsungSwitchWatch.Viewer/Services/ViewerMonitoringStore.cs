using System.Text;
using System.Text.Json;
using SamsungSwitchWatch.Core.Diff;
using SamsungSwitchWatch.Core.Models;
using SamsungSwitchWatch.Core.Parsing;
using SamsungSwitchWatch.Viewer.Models;
using SwitchLinkState = SamsungSwitchWatch.Core.Models.LinkState;

namespace SamsungSwitchWatch.Viewer.Services;

public sealed class ViewerMonitoringStore
{
    private const int CurrentSchemaVersion = 3;
    private const int MaximumStoredEventCount = 500;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _path;
    private readonly IViewerMonitoringPersistence _persistence;
    private MonitoringEnvelope _state;

    public ViewerMonitoringStore(string? path = null)
        : this(path, PhysicalViewerMonitoringPersistence.Instance)
    {
    }

    internal ViewerMonitoringStore(
        string? path,
        IViewerMonitoringPersistence persistence)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SamsungSwitchWatch",
            "viewer-monitor-state.json");
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _state = LoadUnsafe();
    }

    public IReadOnlyList<SwitchEventDto> BeginSession(IReadOnlyList<ManagedDeviceProfile> devices)
    {
        lock (_sync)
        {
            return CommitUnsafe(() =>
            {
                var now = DateTimeOffset.UtcNow;
                var previous = _state.LastStoppedUtc ?? _state.LastHeartbeatUtc;
                var created = new List<SwitchEventDto>();
                if (previous is { } last
                    && now - last > TimeSpan.FromSeconds(10))
                {
                    foreach (var device in devices.Where(item => item.MonitoringEnabled))
                    {
                        RemoveDeviceBaselines(device.Id);
                        created.Add(CreateEvent(
                            device,
                            DeviceHealth.Warning,
                            "감시 공백",
                            "Viewer 종료 중 감시 공백",
                            $"{last.LocalDateTime:MM-dd HH:mm:ss} ~ {now.LocalDateTime:MM-dd HH:mm:ss}"));
                    }
                }
                _state.LastStartedUtc = now;
                _state.LastStoppedUtc = null;
                _state.LastHeartbeatUtc = now;
                return (IReadOnlyList<SwitchEventDto>)created;
            });
        }
    }

    public IReadOnlyList<SwitchEventDto> RecordOutput(
        ManagedDeviceProfile device,
        string command,
        string output) =>
        TryRecordParsedOutput(device, command, output).Events;

    internal MonitoringOutputRecordResult TryRecordParsedOutput(
        ManagedDeviceProfile device,
        string command,
        string output)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        if (IsSyslogCommand(command))
        {
            var parsed = LogOutputParser.Parse(output);
            if (!parsed.IsSuccess || parsed.Value is null)
            {
                return MonitoringOutputRecordResult.Rejected(
                    parsed.Error?.Code ?? "PARSER_UNSUPPORTED");
            }
            return RecordParsedLogOutput(device, command, parsed.Value);
        }

        var interfaces = InterfaceStatusOutputParser.Parse(output);
        if (!interfaces.IsSuccess || interfaces.Value is null)
        {
            return MonitoringOutputRecordResult.Rejected(
                interfaces.Error?.Code ?? "PARSER_UNSUPPORTED");
        }
        return RecordParsedInterfaceOutput(device, command, interfaces.Value);
    }

    private MonitoringOutputRecordResult RecordParsedInterfaceOutput(
        ManagedDeviceProfile device,
        string command,
        InterfaceStatusSnapshot snapshot)
    {
        lock (_sync)
        {
            return CommitUnsafe(() =>
            {
                var created = new List<SwitchEventDto>();
                var key = $"{device.Id}\n{command.Trim().ToUpperInvariant()}";
                if (!_state.Baselines.TryGetValue(key, out var previous))
                {
                    previous = new MonitorBaseline();
                    _state.Baselines[key] = previous;
                }

                var current = snapshot.Interfaces.ToDictionary(
                    item => item.Key,
                    item => StoredInterfaceState.From(item.Value),
                    StringComparer.OrdinalIgnoreCase);

                if (!previous.ParsedInterfaceInitialized)
                {
                    previous.ParsedInterfaceInitialized = true;
                    previous.Interfaces = current;
                    RecoverActiveInterfacesVisibleAsUp(device, current, created);
                    HeartbeatUnsafe();
                    return MonitoringOutputRecordResult.Success(created);
                }

                foreach (var (portId, currentPort) in current)
                {
                    if (!previous.Interfaces.TryGetValue(portId, out var oldPort))
                    {
                        // A newly visible row is baselined without inferring that
                        // a topology or link event occurred.
                        previous.Interfaces[portId] = currentPort;
                        continue;
                    }

                    if (oldPort.OperationalState == SwitchLinkState.Up
                        && currentPort.OperationalState == SwitchLinkState.Down)
                    {
                        OpenInterfaceCondition(
                            device,
                            portId,
                            created);
                    }
                    else if (oldPort.OperationalState == SwitchLinkState.Down
                             && currentPort.OperationalState == SwitchLinkState.Up)
                    {
                        RecoverInterfaceCondition(device, portId, created);
                    }

                    previous.Interfaces[portId] = currentPort;
                }

                // Rows missing from a single collection are retained. Their
                // absence alone is not enough evidence for a link transition.
                HeartbeatUnsafe();
                return MonitoringOutputRecordResult.Success(created);
            });
        }
    }

    private MonitoringOutputRecordResult RecordParsedLogOutput(
        ManagedDeviceProfile device,
        string command,
        LogSnapshot snapshot)
    {
        lock (_sync)
        {
            return CommitUnsafe(() =>
            {
                var created = new List<SwitchEventDto>();
                var key = $"{device.Id}\n{command.Trim().ToUpperInvariant()}";
                if (!_state.Baselines.TryGetValue(key, out var previous))
                {
                    previous = new MonitorBaseline();
                    _state.Baselines[key] = previous;
                }

                var cursor = previous.ParsedLogInitialized
                    ? new LogCursor(
                        true,
                        previous.LogEntryIdentities,
                        previous.LogAwaitingNonEmptyBaseline)
                    : LogCursor.Empty;
                var comparison = LogCursorEngine.Compare(cursor, snapshot);
                previous.ParsedLogInitialized = true;
                previous.LogEntryIdentities = [.. comparison.Cursor.EntryIdentities];
                previous.LogAwaitingNonEmptyBaseline = comparison.Cursor.AwaitingNonEmptyBaseline;

                if (comparison.BufferWasReset)
                {
                    created.Add(CreateEvent(
                        device,
                        DeviceHealth.Warning,
                        "로그 상태",
                        "로그 버퍼 순환 또는 초기화 감지",
                        "이전 기준 로그를 찾지 못해 현재 출력으로 기준을 다시 설정했습니다."));
                }
                if (comparison.NewEntries.Count > 0)
                {
                    created.Add(CreateEvent(
                        device,
                        DeviceHealth.Warning,
                        "새 로그",
                        $"새 시스템 로그 {comparison.NewEntries.Count}건",
                        "이전 조회 이후 로그 목록이 변경되었습니다."));
                }

                HeartbeatUnsafe();
                return MonitoringOutputRecordResult.Success(created);
            });
        }
    }

    public int GetActiveInterfaceConditionCount(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        lock (_sync)
        {
            return _state.ActiveInterfaceConditions.Values.Count(condition =>
                condition.DeviceId.Equals(deviceId, StringComparison.Ordinal));
        }
    }

    private void OpenInterfaceCondition(
        ManagedDeviceProfile device,
        string portId,
        ICollection<SwitchEventDto> created)
    {
        var conditionKey = InterfaceConditionKey(device.Id, portId);
        if (_state.ActiveInterfaceConditions.ContainsKey(conditionKey)) return;

        var item = CreateEvent(
            device,
            DeviceHealth.Warning,
            "포트 상태",
            $"Port {portId} Link Down",
            $"Port {portId} 운영 상태가 Up에서 Down으로 변경되었습니다. "
            + "영향 대상은 지정되지 않았습니다. 포트 사용 여부, 케이블과 상대 장비 상태를 확인하세요.",
            conditionKey,
            true);
        _state.ActiveInterfaceConditions[conditionKey] = new ActiveInterfaceCondition
        {
            DeviceId = device.Id,
            PortId = portId,
            EventId = item.AgentEventId,
            OccurredUtc = item.OccurredAt
        };
        created.Add(item);
    }

    private void RecoverInterfaceCondition(
        ManagedDeviceProfile device,
        string portId,
        ICollection<SwitchEventDto> created)
    {
        var conditionKey = InterfaceConditionKey(device.Id, portId);
        if (!_state.ActiveInterfaceConditions.Remove(conditionKey, out var active)) return;

        var recoveredAt = DateTimeOffset.UtcNow;
        var originalIndex = _state.Events.FindIndex(item =>
            item.AgentEventId.Equals(active.EventId, StringComparison.Ordinal));
        if (originalIndex >= 0)
        {
            var updatedOriginal = _state.Events[originalIndex] with
            {
                Acknowledged = true,
                Recovered = true,
                IsActiveCondition = false,
                RecoveredAt = recoveredAt
            };
            _state.Events[originalIndex] = updatedOriginal;
            created.Add(updatedOriginal);
        }

        var sequence = ++_state.NextSequence;
        var recovery = new SwitchEventDto(
            sequence,
            $"viewer-{sequence}",
            device.Id,
            device.DisplayName,
            recoveredAt,
            DeviceHealth.Normal,
            "복구",
            $"Port {portId} Link 복구",
            $"Port {portId} 운영 상태가 Down에서 Up으로 복구되었습니다.",
            true,
            true,
            conditionKey,
            false,
            recoveredAt,
            active.EventId);
        _state.Events.Add(recovery);
        created.Add(recovery);
    }

    private void RecoverActiveInterfacesVisibleAsUp(
        ManagedDeviceProfile device,
        IReadOnlyDictionary<string, StoredInterfaceState> current,
        ICollection<SwitchEventDto> created)
    {
        foreach (var active in _state.ActiveInterfaceConditions.Values
                     .Where(condition => condition.DeviceId.Equals(device.Id, StringComparison.Ordinal))
                     .ToArray())
        {
            if (current.TryGetValue(active.PortId, out var port)
                && port.OperationalState == SwitchLinkState.Up)
            {
                RecoverInterfaceCondition(device, active.PortId, created);
            }
        }
    }

    private static string InterfaceConditionKey(string deviceId, string portId) =>
        $"{deviceId}\n{portId.ToUpperInvariant()}";

    public IReadOnlyList<SwitchEventDto> RecordFailure(ManagedDeviceProfile device, string code)
    {
        lock (_sync)
        {
            return CommitUnsafe(() =>
            {
                if (_state.ActiveFailures.TryGetValue(device.Id, out var active))
                {
                    if (active.Code.Equals(code, StringComparison.Ordinal))
                    {
                        HeartbeatUnsafe();
                        return (IReadOnlyList<SwitchEventDto>)[];
                    }

                    active.Code = code;
                    var originalIndex = _state.Events.FindIndex(item =>
                        item.AgentEventId.Equals(active.EventId, StringComparison.Ordinal));
                    var updated = new List<SwitchEventDto>();
                    if (originalIndex >= 0)
                    {
                        var changed = _state.Events[originalIndex] with { Detail = code };
                        _state.Events[originalIndex] = changed;
                        updated.Add(changed);
                    }
                    HeartbeatUnsafe();
                    return (IReadOnlyList<SwitchEventDto>)updated;
                }

                var item = CreateEvent(
                    device,
                    DeviceHealth.Disconnected,
                    "수집기",
                    "주기 감시 실패",
                    code);
                _state.ActiveFailures[device.Id] = new ActiveFailure
                {
                    Code = code,
                    EventId = item.AgentEventId,
                    OccurredUtc = item.OccurredAt
                };
                HeartbeatUnsafe();
                return (IReadOnlyList<SwitchEventDto>)[item];
            });
        }
    }

    public IReadOnlyList<SwitchEventDto> RecordSuccess(ManagedDeviceProfile device)
    {
        lock (_sync)
        {
            return CommitUnsafe(() =>
            {
                var created = ResolveFailure(device);
                HeartbeatUnsafe();
                return (IReadOnlyList<SwitchEventDto>)created;
            });
        }
    }

    public IReadOnlyList<SwitchEventDto> LoadEvents(int limit = 500)
    {
        lock (_sync)
        {
            return _state.Events
                .OrderByDescending(item => item.Sequence)
                .Take(Math.Clamp(limit, 1, MaximumStoredEventCount))
                .ToArray();
        }
    }

    public IReadOnlyList<CollectorCapabilityDto> LoadCapabilities(string deviceId)
    {
        lock (_sync)
        {
            return _state.Capabilities.TryGetValue(deviceId, out var capabilities)
                ? capabilities.ToArray()
                : [];
        }
    }

    public string? GetActiveFailureCode(string deviceId)
    {
        lock (_sync)
        {
            return _state.ActiveFailures.TryGetValue(deviceId, out var failure)
                ? failure.Code
                : null;
        }
    }

    public void ClearActiveFailure(string deviceId)
    {
        lock (_sync)
        {
            if (!_state.ActiveFailures.ContainsKey(deviceId)) return;
            CommitUnsafe(() =>
            {
                _state.ActiveFailures.Remove(deviceId);
                HeartbeatUnsafe();
            });
        }
    }

    public void RecordCapability(string deviceId, CollectorCapabilityDto capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(capability);
        lock (_sync)
        {
            var existing = _state.Capabilities.TryGetValue(deviceId, out var current)
                ? current.FirstOrDefault(item =>
                    item.CommandId.Equals(capability.CommandId, StringComparison.Ordinal))
                : null;
            if (existing is not null && CapabilityEquals(existing, capability)) return;

            CommitUnsafe(() =>
            {
                if (!_state.Capabilities.TryGetValue(deviceId, out var capabilities))
                {
                    capabilities = [];
                    _state.Capabilities[deviceId] = capabilities;
                }
                var index = capabilities.FindIndex(item =>
                    item.CommandId.Equals(capability.CommandId, StringComparison.Ordinal));
                if (index >= 0) capabilities[index] = capability;
                else capabilities.Add(capability);
                HeartbeatUnsafe();
            });
        }
    }

    public bool Acknowledge(string eventId)
    {
        lock (_sync)
        {
            var index = _state.Events.FindIndex(item =>
                item.AgentEventId.Equals(eventId, StringComparison.Ordinal));
            if (index < 0) return false;
            return CommitUnsafe(() =>
            {
                var candidateIndex = _state.Events.FindIndex(item =>
                    item.AgentEventId.Equals(eventId, StringComparison.Ordinal));
                _state.Events[candidateIndex] = _state.Events[candidateIndex] with { Acknowledged = true };
                return true;
            });
        }
    }

    public void Heartbeat()
    {
        lock (_sync)
        {
            CommitUnsafe(HeartbeatUnsafe);
        }
    }

    public void EndSession()
    {
        lock (_sync)
        {
            CommitUnsafe(() =>
            {
                _state.LastStoppedUtc = DateTimeOffset.UtcNow;
                _state.LastHeartbeatUtc = _state.LastStoppedUtc;
            });
        }
    }

    private SwitchEventDto CreateEvent(
        ManagedDeviceProfile device,
        DeviceHealth health,
        string kind,
        string title,
        string detail,
        string? conditionKey = null,
        bool isActiveCondition = false)
    {
        var sequence = ++_state.NextSequence;
        var item = new SwitchEventDto(
            sequence,
            $"viewer-{sequence}",
            device.Id,
            device.DisplayName,
            DateTimeOffset.UtcNow,
            health,
            kind,
            title,
            detail,
            false,
            false,
            conditionKey ?? $"{device.Id}:{kind}:{title}",
            isActiveCondition);
        _state.Events.Add(item);
        return item;
    }

    private void TrimEvents()
    {
        var remainingToRemove = _state.Events.Count - MaximumStoredEventCount;
        if (remainingToRemove <= 0)
        {
            return;
        }

        var activeEventIds = _state.ActiveFailures.Values
            .Select(item => item.EventId)
            .Concat(_state.ActiveInterfaceConditions.Values.Select(item => item.EventId))
            .ToHashSet(StringComparer.Ordinal);

        _state.Events.RemoveAll(item =>
        {
            if (remainingToRemove <= 0 || activeEventIds.Contains(item.AgentEventId))
            {
                return false;
            }

            remainingToRemove--;
            return true;
        });
    }

    private List<SwitchEventDto> ResolveFailure(ManagedDeviceProfile device)
    {
        var created = new List<SwitchEventDto>();
        if (!_state.ActiveFailures.Remove(device.Id, out var active)) return created;

        var recoveredAt = DateTimeOffset.UtcNow;
        var originalIndex = _state.Events.FindIndex(item =>
            item.AgentEventId.Equals(active.EventId, StringComparison.Ordinal));
        if (originalIndex >= 0)
        {
            var updatedOriginal = _state.Events[originalIndex] with
            {
                Recovered = true,
                RecoveredAt = recoveredAt
            };
            _state.Events[originalIndex] = updatedOriginal;
            created.Add(updatedOriginal);
        }
        var sequence = ++_state.NextSequence;
        var recovery = new SwitchEventDto(
            sequence,
            $"viewer-{sequence}",
            device.Id,
            device.DisplayName,
            recoveredAt,
            DeviceHealth.Normal,
            "복구",
            "주기 감시 복구",
            $"{active.Code} 상태가 해제되었습니다.",
            true,
            true,
            $"{device.Id}:monitor-failure",
            false,
            recoveredAt,
            active.EventId);
        _state.Events.Add(recovery);
        created.Add(recovery);
        return created;
    }

    private MonitoringEnvelope LoadUnsafe()
    {
        var json = _persistence.ReadIfExists(_path);
        if (json is null) return new MonitoringEnvelope();

        try
        {
            var loaded = JsonSerializer.Deserialize<MonitoringEnvelope>(json, JsonOptions)
                         ?? throw new InvalidDataException("MONITOR_STATE_NULL");
            ValidateState(loaded);
            return CloneState(loaded);
        }
        catch (Exception exception) when (
            exception is JsonException
            or NotSupportedException
            or InvalidDataException)
        {
            _persistence.Quarantine(
                _path,
                _path + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}");
            return new MonitoringEnvelope();
        }
    }

    private void SaveUnsafe(MonitoringEnvelope state)
    {
        _persistence.WriteAtomically(
            _path,
            JsonSerializer.Serialize(state, JsonOptions));
    }

    private TResult CommitUnsafe<TResult>(Func<TResult> mutation)
    {
        var original = _state;
        var candidate = CloneState(original);
        TResult result;
        _state = candidate;
        try
        {
            result = mutation();
            TrimEvents();
        }
        finally
        {
            _state = original;
        }
        SaveUnsafe(candidate);
        _state = candidate;
        return result;
    }

    private void CommitUnsafe(Action mutation)
    {
        CommitUnsafe(() =>
        {
            mutation();
            return true;
        });
    }

    private void RemoveDeviceBaselines(string deviceId)
    {
        var prefix = deviceId + "\n";
        foreach (var key in _state.Baselines.Keys
                     .Where(key => key.StartsWith(prefix, StringComparison.Ordinal))
                     .ToArray())
        {
            _state.Baselines.Remove(key);
        }
    }

    private static MonitoringEnvelope CloneState(MonitoringEnvelope source) =>
        new()
        {
            SchemaVersion = CurrentSchemaVersion,
            NextSequence = source.NextSequence,
            LastStartedUtc = source.LastStartedUtc,
            LastHeartbeatUtc = source.LastHeartbeatUtc,
            LastStoppedUtc = source.LastStoppedUtc,
            Baselines = source.Baselines.ToDictionary(
                item => item.Key,
                item => new MonitorBaseline
                {
                    OutputHash = item.Value.OutputHash,
                    LineHashes = [.. item.Value.LineHashes],
                    ParsedInterfaceInitialized = item.Value.ParsedInterfaceInitialized,
                    Interfaces = item.Value.Interfaces.ToDictionary(
                        port => port.Key,
                        port => new StoredInterfaceState
                        {
                            AdministrativeState = port.Value.AdministrativeState,
                            OperationalState = port.Value.OperationalState,
                            Speed = port.Value.Speed,
                            Duplex = port.Value.Duplex
                        },
                        StringComparer.OrdinalIgnoreCase),
                    ParsedLogInitialized = item.Value.ParsedLogInitialized,
                    LogEntryIdentities = [.. item.Value.LogEntryIdentities],
                    LogAwaitingNonEmptyBaseline = item.Value.LogAwaitingNonEmptyBaseline
                },
                StringComparer.Ordinal),
            ActiveFailures = source.ActiveFailures.ToDictionary(
                item => item.Key,
                item => new ActiveFailure
                {
                    Code = item.Value.Code,
                    EventId = item.Value.EventId,
                    OccurredUtc = item.Value.OccurredUtc
                },
                StringComparer.Ordinal),
            ActiveInterfaceConditions = source.ActiveInterfaceConditions.ToDictionary(
                item => item.Key,
                item => new ActiveInterfaceCondition
                {
                    DeviceId = item.Value.DeviceId,
                    PortId = item.Value.PortId,
                    EventId = item.Value.EventId,
                    OccurredUtc = item.Value.OccurredUtc
                },
                StringComparer.Ordinal),
            Capabilities = source.Capabilities.ToDictionary(
                item => item.Key,
                item => item.Value.ToList(),
                StringComparer.Ordinal),
            Events = [.. source.Events]
        };

    private static void ValidateState(MonitoringEnvelope state)
    {
        if (state.SchemaVersion is < 1 or > CurrentSchemaVersion)
        {
            throw new InvalidDataException("MONITOR_STATE_SCHEMA_UNSUPPORTED");
        }
        if (state.NextSequence < 0
            || state.Baselines is null
            || state.ActiveFailures is null
            || state.ActiveInterfaceConditions is null
            || state.Capabilities is null
            || state.Events is null)
        {
            throw new InvalidDataException("MONITOR_STATE_SCHEMA_INVALID");
        }

        foreach (var (key, baseline) in state.Baselines)
        {
            if (string.IsNullOrWhiteSpace(key)
                || baseline is null
                || baseline.OutputHash is null
                || baseline.LineHashes is null
                || baseline.LineHashes.Any(hash => hash is null)
                || baseline.Interfaces is null
                || baseline.Interfaces.Any(item =>
                    string.IsNullOrWhiteSpace(item.Key)
                    || item.Value is null
                    || !Enum.IsDefined(item.Value.AdministrativeState)
                    || !Enum.IsDefined(item.Value.OperationalState))
                || baseline.LogEntryIdentities is null
                || baseline.LogEntryIdentities.Any(identity =>
                    string.IsNullOrWhiteSpace(identity)))
            {
                throw new InvalidDataException("MONITOR_STATE_BASELINE_INVALID");
            }
        }
        foreach (var (key, active) in state.ActiveFailures)
        {
            if (string.IsNullOrWhiteSpace(key)
                || active is null
                || string.IsNullOrWhiteSpace(active.Code)
                || string.IsNullOrWhiteSpace(active.EventId))
            {
                throw new InvalidDataException("MONITOR_STATE_FAILURE_INVALID");
            }
        }
        foreach (var (key, active) in state.ActiveInterfaceConditions)
        {
            if (string.IsNullOrWhiteSpace(key)
                || active is null
                || string.IsNullOrWhiteSpace(active.DeviceId)
                || string.IsNullOrWhiteSpace(active.PortId)
                || string.IsNullOrWhiteSpace(active.EventId))
            {
                throw new InvalidDataException("MONITOR_STATE_INTERFACE_CONDITION_INVALID");
            }
        }
        foreach (var (key, capabilities) in state.Capabilities)
        {
            if (string.IsNullOrWhiteSpace(key)
                || capabilities is null
                || capabilities.Any(capability =>
                    capability is null || string.IsNullOrWhiteSpace(capability.CommandId)))
            {
                throw new InvalidDataException("MONITOR_STATE_CAPABILITY_INVALID");
            }
        }
        if (state.Events.Any(item =>
                item is null
                || string.IsNullOrWhiteSpace(item.AgentEventId)
                || string.IsNullOrWhiteSpace(item.DeviceId)))
        {
            throw new InvalidDataException("MONITOR_STATE_EVENT_INVALID");
        }
    }

    private void HeartbeatUnsafe() => _state.LastHeartbeatUtc = DateTimeOffset.UtcNow;

    private static bool CapabilityEquals(
        CollectorCapabilityDto left,
        CollectorCapabilityDto right) =>
        left.CommandId == right.CommandId
        && left.Supported == right.Supported
        && left.State == right.State
        && left.ErrorCode == right.ErrorCode
        && left.PrimaryCli == right.PrimaryCli
        && left.SelectedCli == right.SelectedCli
        && left.LastSuccessfulCli == right.LastSuccessfulCli
        && (left.CandidateClis ?? []).SequenceEqual(right.CandidateClis ?? [], StringComparer.OrdinalIgnoreCase);

    public void ClearCapabilities(string deviceId)
    {
        lock (_sync)
        {
            if (!_state.Capabilities.ContainsKey(deviceId)) return;
            CommitUnsafe(() =>
            {
                _state.Capabilities.Remove(deviceId);
                HeartbeatUnsafe();
            });
        }
    }

    private static bool IsSyslogCommand(string command) =>
        command.Contains("sylog", StringComparison.OrdinalIgnoreCase)
        || command.Contains("syslog", StringComparison.OrdinalIgnoreCase)
        || command.TrimStart().StartsWith("show log ", StringComparison.OrdinalIgnoreCase);

    private sealed class MonitoringEnvelope
    {
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public long NextSequence { get; set; }
        public DateTimeOffset? LastStartedUtc { get; set; }
        public DateTimeOffset? LastHeartbeatUtc { get; set; }
        public DateTimeOffset? LastStoppedUtc { get; set; }
        public Dictionary<string, MonitorBaseline> Baselines { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, ActiveFailure> ActiveFailures { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, ActiveInterfaceCondition> ActiveInterfaceConditions { get; set; } =
            new(StringComparer.Ordinal);
        public Dictionary<string, List<CollectorCapabilityDto>> Capabilities { get; set; } =
            new(StringComparer.Ordinal);
        public List<SwitchEventDto> Events { get; set; } = [];
    }

    private sealed class MonitorBaseline
    {
        public string OutputHash { get; set; } = string.Empty;
        public List<string> LineHashes { get; set; } = [];
        public bool ParsedInterfaceInitialized { get; set; }
        public Dictionary<string, StoredInterfaceState> Interfaces { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
        public bool ParsedLogInitialized { get; set; }
        public List<string> LogEntryIdentities { get; set; } = [];
        public bool LogAwaitingNonEmptyBaseline { get; set; }
    }

    private sealed class StoredInterfaceState
    {
        public AdministrativeState AdministrativeState { get; set; }
        public SwitchLinkState OperationalState { get; set; }
        public string? Speed { get; set; }
        public string? Duplex { get; set; }

        public static StoredInterfaceState From(InterfaceStatus status) =>
            new()
            {
                AdministrativeState = status.AdministrativeState,
                OperationalState = status.OperationalState,
                Speed = status.Speed,
                Duplex = status.Duplex
            };
    }

    private sealed class ActiveFailure
    {
        public string Code { get; set; } = string.Empty;
        public string EventId { get; set; } = string.Empty;
        public DateTimeOffset OccurredUtc { get; set; }
    }

    private sealed class ActiveInterfaceCondition
    {
        public string DeviceId { get; set; } = string.Empty;
        public string PortId { get; set; } = string.Empty;
        public string EventId { get; set; } = string.Empty;
        public DateTimeOffset OccurredUtc { get; set; }
    }
}

internal sealed record MonitoringOutputRecordResult(
    bool Accepted,
    string? ErrorCode,
    IReadOnlyList<SwitchEventDto> Events)
{
    public static MonitoringOutputRecordResult Success(IReadOnlyList<SwitchEventDto> events) =>
        new(true, null, events);

    public static MonitoringOutputRecordResult Rejected(string errorCode) =>
        new(false, errorCode, []);
}

internal interface IViewerMonitoringPersistence
{
    string? ReadIfExists(string path);
    void WriteAtomically(string path, string content);
    void Quarantine(string path, string destination);
}

internal sealed class PhysicalViewerMonitoringPersistence : IViewerMonitoringPersistence
{
    public static PhysicalViewerMonitoringPersistence Instance { get; } = new();

    private PhysicalViewerMonitoringPersistence()
    {
    }

    public string? ReadIfExists(string path)
    {
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    public void Quarantine(string path, string destination) =>
        File.Move(path, destination, false);
}
