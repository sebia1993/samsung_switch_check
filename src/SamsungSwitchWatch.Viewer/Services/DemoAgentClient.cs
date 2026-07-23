using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Services;

public sealed class DemoAgentClient : IAgentClient
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<SwitchEventDto> _events;
    private readonly List<AgentEventChangeDto> _changes;
    private Task? _simulation;
    private long _sequence;
    private long _changeSequence;
    private int _step;

    public DemoAgentClient()
    {
        var now = DateTimeOffset.Now;
        _events =
        [
            new(1001, "demo-event-1001", "sw-03", "ACCESS-SW-03", now.AddMinutes(-12), DeviceHealth.Critical, "상태 변경", "업링크 포트 26 DOWN", "동작 상태: UP → DOWN", false, false, "port-26-link"),
            new(1002, "demo-event-1002", "sw-02", "ACCESS-SW-02", now.AddMinutes(-8), DeviceHealth.Warning, "새 로그", "STP Root 변경 감지", "Root Bridge 식별자가 변경되었습니다.", false, false, "stp-root"),
            new(1003, "demo-event-1003", "sw-04", "ACCESS-SW-04", now.AddMinutes(-5), DeviceHealth.Disconnected, "수집기", "스위치 접속 실패", "TCP_TIMEOUT · 마지막 정상 확인 5분 전", true, false, "connection"),
            new(1004, "demo-event-1004", "sw-01", "ACCESS-SW-01", now.AddMinutes(-2), DeviceHealth.Normal, "복구", "포트 17 복구", "동작 상태: DOWN → UP", true, true, "port-17-link")
        ];
        _sequence = _events.Max(item => item.Sequence);
        _changeSequence = _sequence;
        _changes = _events.Select(item => new AgentEventChangeDto(item.Sequence, "Created", item)).ToList();
    }

    public event EventHandler<AgentEventChangeDto>? EventChanged;
    public event EventHandler<AgentConnectionState>? ConnectionStateChanged;
    public bool SupportsStatelessV4 => true;

    public Task<AgentIdentityDto> GetIdentityAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new AgentIdentityDto(
            4,
            "demo-agent",
            "demo-instance",
            new string('A', 64),
            "https",
            8,
            65_536));

    public async Task<TelnetExecutionResultDto> TestTelnetAsync(
        TelnetTargetDto target,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        await Task.Delay(180, cancellationToken);
        return new TelnetExecutionResultDto(
            4, target.RequestId, true, string.IsNullOrEmpty(target.EnablePassword) ? "user" : "privileged",
            string.IsNullOrEmpty(target.EnablePassword) ? ">" : "#",
            started, DateTimeOffset.UtcNow, 180, []);
    }

    public async Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
        TelnetExecuteRequestDto request,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var outputs = new List<TelnetCommandOutputDto>();
        foreach (var command in request.Commands)
        {
            var legacy = await ExecuteReadOnlyQueryAsync(request.RequestId, command, cancellationToken);
            outputs.Add(new TelnetCommandOutputDto(command, legacy.Output, legacy.Truncated, DateTimeOffset.UtcNow));
        }
        return new TelnetExecutionResultDto(
            4, request.RequestId, true, string.IsNullOrEmpty(request.EnablePassword) ? "user" : "privileged",
            string.IsNullOrEmpty(request.EnablePassword) ? ">" : "#",
            started, DateTimeOffset.UtcNow, Math.Max(0, (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds), outputs);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ConnectionStateChanged?.Invoke(this, AgentConnectionState.Demo);
        _simulation ??= Task.Run(SimulateAsync, _lifetime.Token);
        return Task.CompletedTask;
    }

    public Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        IReadOnlyList<DeviceSnapshotDto> devices =
        [
            new("sw-01", "ACCESS-SW-01", "IES4224GP", "192.0.2.11", DeviceHealth.Normal, now.AddSeconds(-8), "등록된 모든 점검 정상", "31일 04:22", Metrics(
                ("Telnet", "정상", DeviceHealth.Normal), ("가동 시간", "31일 04:22", DeviceHealth.Normal), ("포트 24", "UP · 1G/Full", DeviceHealth.Normal), ("신규 로그", "0건", DeviceHealth.Normal)), Capabilities()),
            new("sw-02", "ACCESS-SW-02", "IES4028XP", "장비 주소는 Agent에만 보관", DeviceHealth.Warning, now.AddSeconds(-15), "확인이 필요한 로그 3건", "18일 11:03", Metrics(
                ("Telnet", "정상", DeviceHealth.Normal), ("STP Root", "변경됨", DeviceHealth.Warning), ("PoE 사용", "312 / 370 W", DeviceHealth.Warning), ("신규 로그", "3건", DeviceHealth.Warning)), Capabilities(logSelected: "show log ram")),
            new("sw-03", "ACCESS-SW-03", "IES4226XP", "장비 주소는 Agent에만 보관", DeviceHealth.Critical, now.AddSeconds(-22), "중요 업링크 포트 26 DOWN", "07일 09:41", Metrics(
                ("Telnet", "정상", DeviceHealth.Normal), ("포트 26", "DOWN", DeviceHealth.Critical), ("LACP", "멤버 1개 이탈", DeviceHealth.Critical), ("장애 지속", "12분", DeviceHealth.Critical)), Capabilities(portSelected: "show interfaces status")),
            new("sw-04", "ACCESS-SW-04", "IES4226XP", "장비 주소는 Agent에만 보관", DeviceHealth.Disconnected, now.AddMinutes(-5), "TCP_TIMEOUT · 현재 상태 미확인", "-", Metrics(
                ("Telnet", "연결 실패", DeviceHealth.Disconnected), ("마지막 정상", "5분 전", DeviceHealth.Warning), ("오류 코드", "TCP_TIMEOUT", DeviceHealth.Disconnected), ("다음 재시도", "42초 후", DeviceHealth.Loading)), Capabilities(state: "Failed", errorCode: "TCP_TIMEOUT"))
        ];

        long unacknowledged;
        lock (_events) unacknowledged = _events.LongCount(item => !item.Acknowledged);
        return Task.FromResult(new AgentSnapshotDto(
            now,
            AgentConnectionState.Demo,
            devices,
            _changeSequence,
            "데모 Agent · API v3",
            "API 정상 · 실시간 데모 · 3개 모델",
            "demo-agent",
            AuthoritativeUnacknowledged: unacknowledged,
            ApiVersion: 3,
            AgentChannelStatus: "connected",
            ApiChannelStatus: "available",
            RealtimeChannelStatus: "available",
            OperationalStatuses:
            [
                new("DB_READY", "로컬 상태 DB", "Readiness 정상 · 데모 저장소", DeviceHealth.Normal),
                new("HTTP_UNPROTECTED", "통신 보호", "사내 관리망 전용 · 암호화/인증 없음", DeviceHealth.Warning),
                new("POLLING", "수집 진행", "동시 장비 수집 상한 4대", DeviceHealth.Normal)
            ],
            MaxConcurrentDevices: 4,
            ReadOnlyQueriesEnabled: true));
    }

    public Task<IReadOnlyList<SwitchEventDto>> GetRecentEventsAsync(int limit, CancellationToken cancellationToken)
    {
        lock (_events)
        {
            return Task.FromResult<IReadOnlyList<SwitchEventDto>>(_events
                .OrderByDescending(item => item.Sequence)
                .Take(Math.Clamp(limit, 1, 500))
                .ToArray());
        }
    }

    public Task<EventChangePageDto> GetEventChangesAsync(long cursor, int limit, CancellationToken cancellationToken)
    {
        lock (_events)
        {
            var changes = _changes
                .Where(item => item.ChangeSequence > cursor)
                .OrderBy(item => item.ChangeSequence)
                .Take(Math.Clamp(limit, 1, 500))
                .ToArray();
            var next = changes.Length == 0 ? cursor : changes[^1].ChangeSequence;
            return Task.FromResult(new EventChangePageDto(_changeSequence, next, next < _changeSequence, changes));
        }
    }

    public async Task<CommandResultDto> ExecuteRegisteredCheckAsync(string deviceId, string commandId, CancellationToken cancellationToken)
    {
        await Task.Delay(650, cancellationToken);
        var sequence = Interlocked.Increment(ref _sequence);
        var item = new SwitchEventDto(sequence, $"demo-event-{sequence}", deviceId, DeviceName(deviceId), DateTimeOffset.Now,
            DeviceHealth.Normal, "수동 점검", "등록된 점검 완료", $"{commandId} 명령 프로파일 실행이 정상적으로 완료되었습니다.", true, false, $"manual-{commandId}");
        lock (_events) _events.Add(item);
        var change = Interlocked.Increment(ref _changeSequence);
        var created = new AgentEventChangeDto(change, "Created", item);
        lock (_events) _changes.Add(created);
        EventChanged?.Invoke(this, created);
        return new CommandResultDto(true, "수동 점검이 완료되었습니다.");
    }

    public async Task<ReadOnlyQueryResultDto> ExecuteReadOnlyQueryAsync(
        string deviceId,
        string command,
        CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        await Task.Delay(350, cancellationToken);
        var output = command.Trim().Equals("show port status", StringComparison.OrdinalIgnoreCase)
            ? "Port  Admin  Link  Speed  Duplex\r\n1     Up     Up    1G     Full\r\n24    Up     Up    1G     Full"
            : command.Contains("syslog", StringComparison.OrdinalIgnoreCase)
              || command.Contains("sylog", StringComparison.OrdinalIgnoreCase)
                ? "[100] 14:31:52 Port 24 link up\r\n[99] 14:20:01 System ready"
                : $"Demo read-only result\r\nDevice: {DeviceName(deviceId)}\r\nCommand: {command.Trim()}";
        var completed = DateTimeOffset.UtcNow;
        return new ReadOnlyQueryResultDto(
            3,
            deviceId,
            command.Trim(),
            started,
            completed,
            Math.Max(0, (long)(completed - started).TotalMilliseconds),
            output,
            false,
            1,
            0);
    }

    public Task<bool> AcknowledgeAsync(string eventId, CancellationToken cancellationToken)
    {
        SwitchEventDto? updated = null;
        lock (_events)
        {
            var index = _events.FindIndex(item => string.Equals(item.AgentEventId, eventId, StringComparison.Ordinal));
            if (index < 0) return Task.FromResult(false);
            updated = _events[index] with { Acknowledged = true };
            _events[index] = updated;
        }
        var change = Interlocked.Increment(ref _changeSequence);
        var acknowledged = new AgentEventChangeDto(change, "Acknowledged", updated);
        lock (_events) _changes.Add(acknowledged);
        EventChanged?.Invoke(this, acknowledged);
        return Task.FromResult(true);
    }

    private async Task SimulateAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(12));
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
            {
                var templates = new[]
                {
                    ("sw-02", DeviceHealth.Warning, "새 로그", "PoE 전력 사용률 경고", "전체 예산의 90%를 넘었습니다."),
                    ("sw-03", DeviceHealth.Critical, "상태 변경", "업링크 장애 지속", "포트 26 DOWN · 중복 팝업은 억제됩니다."),
                    ("sw-04", DeviceHealth.Disconnected, "수집기", "연결 재시도 중", "TCP_TIMEOUT · 장비 상태는 미확인입니다."),
                    ("sw-03", DeviceHealth.Normal, "복구", "업링크 복구", "포트 26: DOWN → UP")
                };
                var template = templates[_step++ % templates.Length];
                var sequence = Interlocked.Increment(ref _sequence);
                var item = new SwitchEventDto(sequence, $"demo-event-{sequence}", template.Item1, DeviceName(template.Item1), DateTimeOffset.Now,
                    template.Item2, template.Item3, template.Item4, template.Item5, false, template.Item3 == "복구", template.Item1 + "-demo-condition");
                lock (_events) _events.Add(item);
                var change = Interlocked.Increment(ref _changeSequence);
                var created = new AgentEventChangeDto(change, "Created", item);
                lock (_events) _changes.Add(created);
                EventChanged?.Invoke(this, created);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static IReadOnlyList<DeviceMetricDto> Metrics(params (string label, string value, DeviceHealth health)[] rows) =>
        rows.Select(row => new DeviceMetricDto(row.label, row.value, row.health)).ToArray();

    private static IReadOnlyList<CollectorCapabilityDto> Capabilities(
        string portSelected = "show port status",
        string logSelected = "show syslog tail num 100",
        string state = "Healthy",
        string? errorCode = null)
    {
        var healthy = string.Equals(state, "Healthy", StringComparison.OrdinalIgnoreCase);
        return
    [
        new("version", true, state, errorCode, "show version", healthy ? "show version" : null,
            ["show version"], healthy ? null : "show version"),
        new("system", true, state, errorCode, "show system", healthy ? "show system" : null,
            ["show system"], healthy ? null : "show system"),
        new("interface_status", true, state, errorCode, "show port status", healthy ? portSelected : null,
            ["show port status", "show interfaces status"], healthy ? null : portSelected),
        new("log_ram", true, state, errorCode, "show syslog tail num 100", healthy ? logSelected : null,
            ["show syslog tail num 100", "show log ram"], healthy ? null : logSelected)
    ];
    }

    private static string DeviceName(string id) => id switch
    {
        "sw-01" => "ACCESS-SW-01",
        "sw-02" => "ACCESS-SW-02",
        "sw-03" => "ACCESS-SW-03",
        "sw-04" => "ACCESS-SW-04",
        _ => "SWITCH"
    };

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_simulation is not null)
        {
            try { await _simulation; } catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
    }
}
