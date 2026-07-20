using SamsungSwitchWatch.Core.Events;
using SamsungSwitchWatch.Core.Models;

namespace SamsungSwitchWatch.Core.Diff;

public sealed record ObservationResult(
    DeviceSnapshot Snapshot,
    LogComparison LogComparison,
    IReadOnlyList<MonitorEventDto> Events);

/// <summary>
/// Stateful POC coordinator. Persistence layers can restore the public cursor,
/// previous snapshot and lifecycle tracker after process restart.
/// </summary>
public sealed class DeviceObservationEngine
{
    private readonly DeviceDiffEngine _diffEngine;
    private DeviceSnapshot? _previous;
    private LogCursor _logCursor;

    public DeviceObservationEngine(
        EventLifecycleTracker eventTracker,
        DeviceSnapshot? previous = null,
        LogCursor? logCursor = null)
    {
        _diffEngine = new DeviceDiffEngine(eventTracker);
        _previous = previous;
        _logCursor = logCursor ?? LogCursor.Empty;
    }

    public DeviceSnapshot? PreviousSnapshot => _previous;

    public LogCursor LogCursor => _logCursor;

    public ObservationResult Observe(DeviceSnapshot current)
    {
        ArgumentNullException.ThrowIfNull(current);
        var rebooted = _diffEngine.UptimeDecreased(_previous, current);
        var logComparison = LogCursorEngine.Compare(
            _logCursor,
            current.Logs ?? new LogSnapshot([]),
            forceBaseline: rebooted);
        var events = _diffEngine.Compare(_previous, current, logComparison);
        _previous = current;
        _logCursor = logComparison.Cursor;
        return new ObservationResult(current, logComparison, events);
    }
}
