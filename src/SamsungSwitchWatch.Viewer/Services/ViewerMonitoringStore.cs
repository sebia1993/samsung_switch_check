using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Services;

public sealed class ViewerMonitoringStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _sync = new();
    private readonly string _path;
    private MonitoringEnvelope _state;

    public ViewerMonitoringStore(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SamsungSwitchWatch",
            "viewer-monitor-state.json");
        _state = LoadUnsafe();
    }

    public IReadOnlyList<SwitchEventDto> BeginSession(IReadOnlyList<ManagedDeviceProfile> devices)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            var previous = _state.LastStoppedUtc ?? _state.LastHeartbeatUtc;
            var created = new List<SwitchEventDto>();
            if (previous is { } last
                && now - last > TimeSpan.FromSeconds(10))
            {
                foreach (var device in devices.Where(item => item.MonitoringEnabled))
                {
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
            SaveUnsafe();
            return created;
        }
    }

    public IReadOnlyList<SwitchEventDto> RecordOutput(
        ManagedDeviceProfile device,
        string command,
        string output)
    {
        lock (_sync)
        {
            var created = ResolveFailure(device);
            var key = $"{device.Id}\n{command.Trim().ToUpperInvariant()}";
            var normalizedLines = NormalizeLines(output);
            var hash = Hash(string.Join('\n', normalizedLines));
            // Keep one hash per occurrence. A syslog can legitimately contain
            // the same message more than once, so de-duplicating these hashes
            // would hide a newly repeated event. Only hashes are persisted;
            // command output never leaves memory.
            var lineHashes = normalizedLines.Select(Hash).Take(500).ToList();
            if (!_state.Baselines.TryGetValue(key, out var previous))
            {
                _state.Baselines[key] = new MonitorBaseline { OutputHash = hash, LineHashes = lineHashes };
                HeartbeatUnsafe();
                SaveUnsafe();
                return created;
            }
            if (previous.OutputHash.Equals(hash, StringComparison.Ordinal))
            {
                HeartbeatUnsafe();
                SaveUnsafe();
                return created;
            }

            SwitchEventDto item;
            if (IsSyslogCommand(command))
            {
                var previousCounts = CountOccurrences(previous.LineHashes);
                var currentCounts = CountOccurrences(lineHashes);
                var hasOverlap = currentCounts.Keys.Any(previousCounts.ContainsKey);

                // An empty tail, or a completely unrelated tail, can mean the
                // device cleared or rotated its bounded syslog buffer. There
                // is no reliable cursor in that case, so establish a fresh
                // baseline instead of reporting historical lines as new.
                if (previous.LineHashes.Count == 0)
                {
                    _state.Baselines[key] = new MonitorBaseline { OutputHash = hash, LineHashes = lineHashes };
                    HeartbeatUnsafe();
                    SaveUnsafe();
                    return created;
                }
                if (lineHashes.Count == 0 || !hasOverlap)
                {
                    var reset = CreateEvent(
                        device,
                        DeviceHealth.Warning,
                        "로그 상태",
                        "로그 버퍼 순환 또는 초기화 감지",
                        "이전 기준 로그를 찾지 못해 현재 출력으로 기준을 다시 설정했습니다.");
                    _state.Baselines[key] = new MonitorBaseline { OutputHash = hash, LineHashes = lineHashes };
                    created.Add(reset);
                    HeartbeatUnsafe();
                    SaveUnsafe();
                    return created;
                }

                var added = currentCounts.Sum(item =>
                    Math.Max(0, item.Value - previousCounts.GetValueOrDefault(item.Key)));
                if (added == 0)
                {
                    // Pure reordering and a shorter/truncated tail are both
                    // baseline updates, not new-log events.
                    _state.Baselines[key] = new MonitorBaseline { OutputHash = hash, LineHashes = lineHashes };
                    HeartbeatUnsafe();
                    SaveUnsafe();
                    return created;
                }

                item = CreateEvent(
                    device,
                    DeviceHealth.Warning,
                    "새 로그",
                    $"새 시스템 로그 {added}건",
                    "이전 조회 이후 로그 목록이 변경되었습니다.");
            }
            else
            {
                item = CreateEvent(
                    device,
                    DeviceHealth.Warning,
                    "상태 변경",
                    "포트 상태 변경 감지",
                    "show port status 기준값과 현재 결과가 다릅니다.");
            }
            _state.Baselines[key] = new MonitorBaseline { OutputHash = hash, LineHashes = lineHashes };
            created.Add(item);
            HeartbeatUnsafe();
            SaveUnsafe();
            return created;
        }
    }

    public IReadOnlyList<SwitchEventDto> RecordFailure(ManagedDeviceProfile device, string code)
    {
        lock (_sync)
        {
            if (_state.ActiveFailures.TryGetValue(device.Id, out var active)
                && active.Code.Equals(code, StringComparison.Ordinal))
            {
                HeartbeatUnsafe();
                SaveUnsafe();
                return [];
            }
            var created = ResolveFailure(device);
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
            created.Add(item);
            HeartbeatUnsafe();
            SaveUnsafe();
            return created;
        }
    }

    public IReadOnlyList<SwitchEventDto> RecordSuccess(ManagedDeviceProfile device)
    {
        lock (_sync)
        {
            var created = ResolveFailure(device);
            HeartbeatUnsafe();
            SaveUnsafe();
            return created;
        }
    }

    public IReadOnlyList<SwitchEventDto> LoadEvents(int limit = 500)
    {
        lock (_sync)
        {
            return _state.Events
                .OrderByDescending(item => item.Sequence)
                .Take(Math.Clamp(limit, 1, 500))
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
            if (!_state.ActiveFailures.Remove(deviceId)) return;
            HeartbeatUnsafe();
            SaveUnsafe();
        }
    }

    public void RecordCapability(string deviceId, CollectorCapabilityDto capability)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentNullException.ThrowIfNull(capability);
        lock (_sync)
        {
            if (!_state.Capabilities.TryGetValue(deviceId, out var capabilities))
            {
                capabilities = [];
                _state.Capabilities[deviceId] = capabilities;
            }
            var index = capabilities.FindIndex(item =>
                item.CommandId.Equals(capability.CommandId, StringComparison.Ordinal));
            if (index >= 0 && CapabilityEquals(capabilities[index], capability)) return;
            if (index >= 0) capabilities[index] = capability;
            else capabilities.Add(capability);
            HeartbeatUnsafe();
            SaveUnsafe();
        }
    }

    public bool Acknowledge(string eventId)
    {
        lock (_sync)
        {
            var index = _state.Events.FindIndex(item =>
                item.AgentEventId.Equals(eventId, StringComparison.Ordinal));
            if (index < 0) return false;
            _state.Events[index] = _state.Events[index] with { Acknowledged = true };
            SaveUnsafe();
            return true;
        }
    }

    public void Heartbeat()
    {
        lock (_sync)
        {
            HeartbeatUnsafe();
            SaveUnsafe();
        }
    }

    public void EndSession()
    {
        lock (_sync)
        {
            _state.LastStoppedUtc = DateTimeOffset.UtcNow;
            _state.LastHeartbeatUtc = _state.LastStoppedUtc;
            SaveUnsafe();
        }
    }

    private SwitchEventDto CreateEvent(
        ManagedDeviceProfile device,
        DeviceHealth health,
        string kind,
        string title,
        string detail)
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
            $"{device.Id}:{kind}:{title}");
        _state.Events.Add(item);
        if (_state.Events.Count > 500)
        {
            _state.Events.RemoveRange(0, _state.Events.Count - 500);
        }
        return item;
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
        try
        {
            if (!File.Exists(_path)) return new MonitoringEnvelope();
            return JsonSerializer.Deserialize<MonitoringEnvelope>(
                       File.ReadAllText(_path, Encoding.UTF8), JsonOptions)
                   ?? new MonitoringEnvelope();
        }
        catch
        {
            try
            {
                File.Move(_path, _path + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}", false);
            }
            catch { }
            return new MonitoringEnvelope();
        }
    }

    private void SaveUnsafe()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var temporary = _path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state, JsonOptions), new UTF8Encoding(false));
            File.Move(temporary, _path, true);
        }
        finally
        {
            try { File.Delete(temporary); } catch { }
        }
    }

    private void HeartbeatUnsafe() => _state.LastHeartbeatUtc = DateTimeOffset.UtcNow;

    private static IReadOnlyList<string> NormalizeLines(string output) =>
        (output ?? string.Empty)
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => line.Length > 0)
        .Take(500)
        .ToArray();

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static Dictionary<string, int> CountOccurrences(IEnumerable<string> hashes)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var hash in hashes)
        {
            counts[hash] = counts.GetValueOrDefault(hash) + 1;
        }
        return counts;
    }

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
            if (!_state.Capabilities.Remove(deviceId)) return;
            HeartbeatUnsafe();
            SaveUnsafe();
        }
    }

    private static bool IsSyslogCommand(string command) =>
        command.Contains("sylog", StringComparison.OrdinalIgnoreCase)
        || command.Contains("syslog", StringComparison.OrdinalIgnoreCase);

    private sealed class MonitoringEnvelope
    {
        public int SchemaVersion { get; set; } = 2;
        public long NextSequence { get; set; }
        public DateTimeOffset? LastStartedUtc { get; set; }
        public DateTimeOffset? LastHeartbeatUtc { get; set; }
        public DateTimeOffset? LastStoppedUtc { get; set; }
        public Dictionary<string, MonitorBaseline> Baselines { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, ActiveFailure> ActiveFailures { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<CollectorCapabilityDto>> Capabilities { get; set; } =
            new(StringComparer.Ordinal);
        public List<SwitchEventDto> Events { get; set; } = [];
    }

    private sealed class MonitorBaseline
    {
        public string OutputHash { get; set; } = string.Empty;
        public List<string> LineHashes { get; set; } = [];
    }

    private sealed class ActiveFailure
    {
        public string Code { get; set; } = string.Empty;
        public string EventId { get; set; } = string.Empty;
        public DateTimeOffset OccurredUtc { get; set; }
    }
}
