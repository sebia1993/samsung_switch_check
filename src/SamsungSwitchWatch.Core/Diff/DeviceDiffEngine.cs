using SamsungSwitchWatch.Core.Diagnostics;
using SamsungSwitchWatch.Core.Events;
using SamsungSwitchWatch.Core.Models;

namespace SamsungSwitchWatch.Core.Diff;

public sealed class DeviceDiffEngine(EventLifecycleTracker eventTracker)
{
    private readonly EventLifecycleTracker _eventTracker = eventTracker ?? throw new ArgumentNullException(nameof(eventTracker));

    public IReadOnlyList<MonitorEventDto> Compare(
        DeviceSnapshot? previous,
        DeviceSnapshot current,
        LogComparison logComparison)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(logComparison);
        if (previous is null)
        {
            return [];
        }

        var events = new List<MonitorEventDto>();
        AddRebootEvent(previous, current, events);
        AddLogEvents(current, logComparison, events);
        AddInterfaceEvents(previous, current, events);
        return events;
    }

    public bool UptimeDecreased(DeviceSnapshot? previous, DeviceSnapshot current)
    {
        var oldUptime = previous?.System?.Uptime;
        var newUptime = current.System?.Uptime;
        return oldUptime.HasValue && newUptime.HasValue && newUptime.Value < oldUptime.Value;
    }

    private void AddRebootEvent(
        DeviceSnapshot previous,
        DeviceSnapshot current,
        ICollection<MonitorEventDto> events)
    {
        if (!UptimeDecreased(previous, current))
        {
            return;
        }

        events.Add(_eventTracker.EmitOneShot(
            current.DeviceId,
            $"{current.DeviceId}/reboot/{current.CollectedAt:O}",
            MonitorEventKind.DeviceRebooted,
            MonitorEventSeverity.Critical,
            "장비 재부팅 감지",
            "이전 점검보다 Uptime이 감소했습니다.",
            current.CollectedAt,
            previous.System!.Uptime?.ToString(),
            current.System!.Uptime?.ToString()));
    }

    private void AddLogEvents(
        DeviceSnapshot current,
        LogComparison comparison,
        ICollection<MonitorEventDto> events)
    {
        if (comparison.BufferWasReset)
        {
            events.Add(_eventTracker.EmitOneShot(
                current.DeviceId,
                $"{current.DeviceId}/log-buffer-reset/{current.CollectedAt:O}",
                MonitorEventKind.LogBufferReset,
                MonitorEventSeverity.Warning,
                "로그 버퍼 기준점 재설정",
                "이전 RAM 로그 기준점을 찾지 못해 현재 로그를 새 기준으로 저장했습니다.",
                current.CollectedAt));
        }

        foreach (var entry in comparison.NewEntries)
        {
            events.Add(_eventTracker.EmitOneShot(
                current.DeviceId,
                $"{current.DeviceId}/log/{entry.Identity}",
                MonitorEventKind.NewLog,
                MonitorEventSeverity.Information,
                "새 스위치 로그",
                DiagnosticRedactor.Redact(entry.Message),
                current.CollectedAt));
        }
    }

    private void AddInterfaceEvents(
        DeviceSnapshot previous,
        DeviceSnapshot current,
        ICollection<MonitorEventDto> events)
    {
        if (previous.Interfaces is null || current.Interfaces is null)
        {
            return;
        }

        foreach (var (portId, currentPort) in current.Interfaces.Interfaces)
        {
            if (!previous.Interfaces.Interfaces.TryGetValue(portId, out var previousPort))
            {
                continue;
            }

            var condition = $"{current.DeviceId}/interface/{portId}/oper-down";
            if (previousPort.OperationalState == LinkState.Up && currentPort.OperationalState == LinkState.Down)
            {
                var opened = _eventTracker.OpenCondition(
                    current.DeviceId,
                    condition,
                    MonitorEventKind.InterfaceLink,
                    MonitorEventSeverity.Critical,
                    $"Port {portId} Link Down",
                    $"Port {portId} 운영 상태가 Down으로 변경되었습니다.",
                    current.CollectedAt,
                    "Up",
                    "Down");
                if (opened is not null)
                {
                    events.Add(opened);
                }
            }
            else if (previousPort.OperationalState == LinkState.Down && currentPort.OperationalState == LinkState.Up)
            {
                var recovered = _eventTracker.RecoverCondition(
                    condition,
                    current.CollectedAt,
                    $"Port {portId} 운영 상태가 복구되었습니다.",
                    "Down",
                    "Up");
                if (recovered is not null)
                {
                    events.Add(recovered);
                }
            }
        }
    }
}
