using SamsungSwitchWatch.Core.Diff;
using SamsungSwitchWatch.Core.Events;
using SamsungSwitchWatch.Core.Models;
using SamsungSwitchWatch.Core.Parsing;

namespace SamsungSwitchWatch.Core.Tests;

public sealed class DiffEngineTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 7, 20, 14, 0, 0, TimeSpan.FromHours(9));

    [Fact]
    public void FirstObservation_IsBaselineAndDoesNotAlert()
    {
        var tracker = new EventLifecycleTracker();
        var engine = new DeviceObservationEngine(tracker);
        var snapshot = Snapshot(BaseTime, SyntheticOutputs.SystemTenHours, SyntheticOutputs.InterfacesUp,
            SyntheticOutputs.Logs((1, "13:50:00", "Existing entry")));

        var result = engine.Observe(snapshot);

        Assert.True(result.LogComparison.WasBaselined);
        Assert.Empty(result.LogComparison.NewEntries);
        Assert.Empty(result.Events);
        Assert.Equal(0, tracker.LastSequence);
    }

    [Fact]
    public void NewLogAfterBaseline_EmitsOnlyTheAddition()
    {
        var tracker = new EventLifecycleTracker();
        var engine = new DeviceObservationEngine(tracker);
        engine.Observe(Snapshot(BaseTime, SyntheticOutputs.SystemTenHours, SyntheticOutputs.InterfacesUp,
            SyntheticOutputs.Logs((1, "13:50:00", "Existing entry"))));

        var result = engine.Observe(Snapshot(BaseTime.AddMinutes(1), SyntheticOutputs.SystemTenHours, SyntheticOutputs.InterfacesUp,
            SyntheticOutputs.Logs(
                (2, "14:01:00", "Port 7 link down at 10.10.4.5"),
                (1, "13:50:00", "Existing entry"))));

        var item = Assert.Single(result.Events);
        Assert.Equal(MonitorEventKind.NewLog, item.Kind);
        Assert.Equal(MonitorEventStatus.New, item.Status);
        Assert.DoesNotContain("10.10.4.5", item.Message, StringComparison.Ordinal);
        Assert.Equal(1, item.Sequence);
    }

    [Fact]
    public void LogRotation_ResetsBaselineAndDoesNotReplayWholeBuffer()
    {
        var initial = LogOutputParser.Parse(SyntheticOutputs.Logs(
            (1, "13:50:00", "Old one"),
            (2, "13:51:00", "Old two"))).Value!;
        var first = LogCursorEngine.Compare(null, initial);
        var rotated = LogOutputParser.Parse(SyntheticOutputs.Logs(
            (90, "14:10:00", "Rotated one"),
            (91, "14:11:00", "Rotated two"))).Value!;

        var result = LogCursorEngine.Compare(first.Cursor, rotated);

        Assert.True(result.BufferWasReset);
        Assert.True(result.WasBaselined);
        Assert.Empty(result.NewEntries);
    }

    [Fact]
    public void ClearedLogBuffer_BaselinesFirstRefillInsteadOfReplayingIt()
    {
        var initial = LogOutputParser.Parse(SyntheticOutputs.Logs((1, "13:50:00", "Old one"))).Value!;
        var first = LogCursorEngine.Compare(null, initial);
        var cleared = LogCursorEngine.Compare(first.Cursor, new LogSnapshot([]));

        var refill = LogOutputParser.Parse(SyntheticOutputs.Logs((1, "14:10:00", "Buffer restarted"))).Value!;
        var result = LogCursorEngine.Compare(cleared.Cursor, refill);

        Assert.True(cleared.BufferWasReset);
        Assert.True(result.WasBaselined);
        Assert.Empty(result.NewEntries);
    }

    [Fact]
    public void UptimeDecrease_EmitsRebootAndRebaselinesLogs()
    {
        var tracker = new EventLifecycleTracker();
        var engine = new DeviceObservationEngine(tracker);
        engine.Observe(Snapshot(BaseTime, SyntheticOutputs.SystemTenHours, SyntheticOutputs.InterfacesUp,
            SyntheticOutputs.Logs((1, "13:50:00", "Before reboot"))));

        var result = engine.Observe(Snapshot(BaseTime.AddMinutes(1), SyntheticOutputs.SystemOneMinute, SyntheticOutputs.InterfacesUp,
            SyntheticOutputs.Logs((1, "14:01:00", "Boot complete"))));

        var reboot = Assert.Single(result.Events);
        Assert.Equal(MonitorEventKind.DeviceRebooted, reboot.Kind);
        Assert.True(result.LogComparison.WasBaselined);
        Assert.False(result.LogComparison.BufferWasReset);
        Assert.Empty(result.LogComparison.NewEntries);
    }

    [Fact]
    public void LinkDownAcknowledgedAndUp_UsesOneLifecycleAndMonotonicSequences()
    {
        var tracker = new EventLifecycleTracker(40);
        var engine = new DeviceObservationEngine(tracker);
        engine.Observe(Snapshot(BaseTime, SyntheticOutputs.SystemTenHours, SyntheticOutputs.InterfacesUp, "No log entries"));

        var down = Assert.Single(engine.Observe(Snapshot(
            BaseTime.AddMinutes(1),
            SyntheticOutputs.SystemTenHours,
            SyntheticOutputs.InterfacesPort24Down,
            "No log entries")).Events);
        Assert.Equal(MonitorEventStatus.New, down.Status);
        Assert.Equal(41, down.Sequence);

        var acknowledged = tracker.Acknowledge(down.EventId, "operator", BaseTime.AddMinutes(2));
        Assert.NotNull(acknowledged);
        Assert.Equal(MonitorEventStatus.Acknowledged, acknowledged!.Status);
        Assert.Equal(42, acknowledged.Sequence);

        var stillDown = engine.Observe(Snapshot(
            BaseTime.AddMinutes(3),
            SyntheticOutputs.SystemTenHours,
            SyntheticOutputs.InterfacesPort24Down,
            "No log entries"));
        Assert.Empty(stillDown.Events);

        var recovered = Assert.Single(engine.Observe(Snapshot(
            BaseTime.AddMinutes(4),
            SyntheticOutputs.SystemTenHours,
            SyntheticOutputs.InterfacesUp,
            "No log entries")).Events);
        Assert.Equal(down.EventId, recovered.EventId);
        Assert.Equal(MonitorEventStatus.Recovered, recovered.Status);
        Assert.Equal(43, recovered.Sequence);
        Assert.Equal("operator", recovered.AcknowledgedBy);
        Assert.Empty(tracker.ActiveEvents);
    }

    private static DeviceSnapshot Snapshot(
        DateTimeOffset collectedAt,
        string systemOutput,
        string interfaceOutput,
        string logOutput)
    {
        return new DeviceSnapshot(
            "ACCESS-SW-01",
            collectedAt,
            VersionOutputParser.Parse(SyntheticOutputs.Version).Value,
            SystemOutputParser.Parse(systemOutput).Value,
            LogOutputParser.Parse(logOutput).Value,
            InterfaceStatusOutputParser.Parse(interfaceOutput).Value,
            []);
    }
}
