using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Collections.Concurrent;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class DashboardViewModelTests
{
    [Fact]
    public void StatusAggregator_CountsEveryOperationalState()
    {
        var now = DateTimeOffset.UtcNow;
        var devices = new[]
        {
            Device("a", DeviceHealth.Normal, now),
            Device("b", DeviceHealth.Normal, now),
            Device("c", DeviceHealth.Warning, now),
            Device("d", DeviceHealth.Critical, now),
            Device("e", DeviceHealth.Disconnected, now),
            Device("f", DeviceHealth.Loading, now)
        }.Select(item => new DeviceViewModel(item));

        var result = StatusAggregator.Aggregate(devices);

        Assert.Equal(6, result.Total);
        Assert.Equal(2, result.Normal);
        Assert.Equal(1, result.Warning);
        Assert.Equal(1, result.Critical);
        Assert.Equal(1, result.Disconnected);
        Assert.Equal(1, result.Loading);
        Assert.Equal(3, result.ProblemCount);
    }

    [Fact]
    public async Task ApplyEvents_DeduplicatesByEventIdAndKeepsNewestFirst()
    {
        using var fixture = new ViewModelFixture();
        var viewModel = fixture.CreateViewModel();
        var now = DateTimeOffset.UtcNow;

        viewModel.ApplyEvents([
            Event(7, now.AddSeconds(-2)),
            Event(9, now),
            Event(8, now.AddSeconds(-1)),
            Event(9, now)
        ]);

        Assert.Equal([9L, 8L, 7L], viewModel.RecentEvents.Select(item => item.Sequence));
        Assert.Equal(3, viewModel.UnacknowledgedCount);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task InitializeAsync_LoadsRecentStateThenOrderedCatchup()
    {
        using var fixture = new ViewModelFixture();
        var now = DateTimeOffset.UtcNow;
        fixture.Client.Snapshot = Snapshot(now, highWatermark: 15,
            [Device("a", DeviceHealth.Warning, now), Device("b", DeviceHealth.Normal, now)]);
        fixture.Client.Recent = [Event(12, now.AddSeconds(-3))];
        fixture.Client.Changes.AddRange([
            Change(13, Event(13, now.AddSeconds(-2))),
            Change(15, Event(15, now)),
            Change(14, Event(14, now.AddSeconds(-1)))
        ]);
        var settings = CursorSettings(12);
        var viewModel = fixture.CreateViewModel(settings);

        await viewModel.InitializeAsync();

        Assert.Equal(2, viewModel.TotalCount);
        Assert.Equal(1, viewModel.WarningCount);
        Assert.Equal([15L, 14L, 13L, 12L], viewModel.RecentEvents.Select(item => item.Sequence));
        Assert.Equal(15, viewModel.AppliedChangeCursor);
        Assert.Equal(AgentConnectionState.Connected, viewModel.ConnectionState);
        Assert.True(fixture.Client.StartCalled);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task InitializeAsync_DoesNotReuseLegacyEventSequenceAsV2ChangeCursor()
    {
        using var fixture = new ViewModelFixture();
        fixture.Client.Snapshot = Snapshot(DateTimeOffset.UtcNow, highWatermark: 10, []);
        var settings = new ViewerSettings { DemoMode = true, LastEventSequence = 999 };
        var viewModel = fixture.CreateViewModel(settings);

        await viewModel.InitializeAsync();

        Assert.Equal(10, viewModel.AppliedChangeCursor);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task InitializeAsync_MigratedLegacyPinCursorUsesNewIdentityHighWatermarkBaseline()
    {
        using var fixture = new ViewModelFixture();
        fixture.Client.Snapshot = Snapshot(DateTimeOffset.UtcNow, highWatermark: 25, []);
        fixture.Client.Changes.Add(Change(24, Event(24, DateTimeOffset.UtcNow.AddMinutes(-1))));
        var settings = new ViewerSettings
        {
            DemoMode = true,
            AgentUri = "http://agent.example.test:18443",
            LastEventSequence = 24,
            EventCursors = new Dictionary<string, long> { ["LEGACY-URI-PIN-IDENTITY"] = 24 }
        };
        var viewModel = fixture.CreateViewModel(settings);
        var alerts = 0;
        viewModel.AlertRaised += (_, _) => alerts++;

        await viewModel.InitializeAsync();

        Assert.Equal(25, viewModel.AppliedChangeCursor);
        Assert.True(viewModel.CurrentSettings.TryGetEventCursor("fake-agent", out var migratedCursor));
        Assert.Equal(25, migratedCursor);
        Assert.Equal(0, alerts);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task DemoOperationalStatus_ContainsSingleHttpUnprotectedWarning()
    {
        using var fixture = new ViewModelFixture();
        fixture.Client.Snapshot = Snapshot(DateTimeOffset.UtcNow, 0, []) with
        {
            OperationalStatuses =
            [
                new("HTTP_UNPROTECTED", "통신 보호", "사내 관리망 전용", DeviceHealth.Warning)
            ]
        };
        var viewModel = fixture.CreateViewModel(new ViewerSettings { DemoMode = true });

        await viewModel.InitializeAsync();

        Assert.Single(viewModel.OperationalStatuses, status =>
            status.Code == "HTTP_UNPROTECTED");
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task Catchup_PagesMoreThan1200ChangesWithoutAdvancingAcrossGap()
    {
        using var fixture = new ViewModelFixture();
        var now = DateTimeOffset.UtcNow;
        fixture.Client.Changes.AddRange(Enumerable.Range(1, 1201)
            .Select(value => Change(value, Event(value, now.AddSeconds(value)))));
        fixture.Client.Snapshot = Snapshot(now, 1201, []);
        var viewModel = fixture.CreateViewModel(CursorSettings(0));

        await viewModel.InitializeAsync();

        Assert.Equal(1201, viewModel.AppliedChangeCursor);
        Assert.Equal(500, viewModel.RecentEvents.Count);
        Assert.Equal(1201, viewModel.RecentEvents[0].Sequence);
        Assert.True(fixture.Client.ChangePageCalls >= 3);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task RetentionGap_ReloadsRecentStateAndAdvancesToServerResetCursor()
    {
        using var fixture = new ViewModelFixture();
        var now = DateTimeOffset.UtcNow;
        fixture.Client.Snapshot = Snapshot(now, 250, []);
        fixture.Client.Recent = [Event(42, now)];
        fixture.Client.ChangePages.Enqueue(new EventChangePageDto(250, 250, false, [], true, 250));
        var viewModel = fixture.CreateViewModel(CursorSettings(10));

        await viewModel.InitializeAsync();

        Assert.Equal(250, viewModel.AppliedChangeCursor);
        Assert.Equal("event-42", Assert.Single(viewModel.RecentEvents).AgentEventId);
        Assert.Contains("EVENT_FEED_RESET", viewModel.OperationMessage, StringComparison.Ordinal);
        Assert.Equal(AgentConnectionState.Connected, viewModel.ConnectionState);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task ManualRefresh_ReconcilesRecentEventsAfterServerRetention()
    {
        using var fixture = new ViewModelFixture();
        var now = DateTimeOffset.UtcNow;
        fixture.Client.Snapshot = Snapshot(now, 1, []);
        fixture.Client.Recent = [Event(1, now)];
        var viewModel = fixture.CreateViewModel(CursorSettings(1));
        await viewModel.InitializeAsync();
        Assert.Single(viewModel.RecentEvents);

        fixture.Client.Recent = [];
        viewModel.RefreshCommand.Execute(null);
        await WaitUntilAsync(() => fixture.Client.RecentCalls >= 2 && !viewModel.IsBusy);

        Assert.Empty(viewModel.RecentEvents);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task RefreshRecentSnapshot_CannotOverwriteConcurrentAcknowledgement()
    {
        using var fixture = new ViewModelFixture();
        var now = DateTimeOffset.UtcNow;
        var original = Event(1, now) with { Acknowledged = false };
        fixture.Client.Snapshot = Snapshot(now, 1, []);
        fixture.Client.Recent = [original];
        var viewModel = fixture.CreateViewModel(CursorSettings(1));
        await viewModel.InitializeAsync();

        fixture.Client.BlockRecentAfterCalls = 1;
        viewModel.RefreshCommand.Execute(null);
        await fixture.Client.RecentBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Client.Emit(Change(2, original with { Acknowledged = true }, "Acknowledged"));
        fixture.Client.ReleaseRecent.TrySetResult();

        await WaitUntilAsync(() => viewModel.AppliedChangeCursor == 2 && !viewModel.IsBusy);
        Assert.True(Assert.Single(viewModel.RecentEvents).Acknowledged);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task LiveOutOfOrderChange_WaitsForMissingSequenceThenAppliesBoth()
    {
        using var fixture = new ViewModelFixture();
        fixture.Client.Snapshot = Snapshot(DateTimeOffset.UtcNow, 0, []);
        var viewModel = fixture.CreateViewModel(CursorSettings(0));
        await viewModel.InitializeAsync();
        var now = DateTimeOffset.UtcNow;

        fixture.Client.Emit(Change(2, Event(2, now)));
        await Task.Delay(50);
        Assert.Equal(0, viewModel.AppliedChangeCursor);

        fixture.Client.Emit(Change(1, Event(1, now.AddSeconds(-1))));
        await WaitUntilAsync(() => viewModel.AppliedChangeCursor == 2);

        Assert.Equal([2L, 1L], viewModel.RecentEvents.Select(item => item.Sequence));
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task AcknowledgedAndRecoveredChanges_UpdateSameEventWithoutDuplicate()
    {
        using var fixture = new ViewModelFixture();
        fixture.Client.Snapshot = Snapshot(DateTimeOffset.UtcNow, 0, []);
        var viewModel = fixture.CreateViewModel(CursorSettings(0));
        await viewModel.InitializeAsync();
        var original = Event(1, DateTimeOffset.UtcNow) with { Severity = DeviceHealth.Critical, IsActiveCondition = true };

        fixture.Client.Emit(Change(1, original));
        fixture.Client.Emit(Change(2, original with { Acknowledged = true }, "Acknowledged"));
        fixture.Client.Emit(Change(3, original with { Acknowledged = true, Recovered = true, Severity = DeviceHealth.Normal, IsActiveCondition = false }, "Recovered"));
        await WaitUntilAsync(() => viewModel.AppliedChangeCursor == 3);

        var item = Assert.Single(viewModel.RecentEvents);
        Assert.True(item.Acknowledged);
        Assert.True(item.Recovered);
        Assert.Equal(DeviceHealth.Normal, item.Severity);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task Reconnected_PerformsCatchupWithoutViewerRestart()
    {
        using var fixture = new ViewModelFixture();
        fixture.Client.Snapshot = Snapshot(DateTimeOffset.UtcNow, 0, []);
        var viewModel = fixture.CreateViewModel(CursorSettings(0));
        await viewModel.InitializeAsync();

        fixture.Client.EmitState(AgentConnectionState.Offline);
        Assert.Equal(AgentConnectionState.Stale, viewModel.ConnectionState);
        fixture.Client.Changes.Add(Change(1, Event(1, DateTimeOffset.UtcNow)));
        fixture.Client.Snapshot = Snapshot(DateTimeOffset.UtcNow, 1, []);
        fixture.Client.EmitState(AgentConnectionState.Connected);
        await WaitUntilAsync(() => viewModel.AppliedChangeCursor == 1);

        Assert.Equal(AgentConnectionState.Connected, viewModel.ConnectionState);
        Assert.Single(viewModel.RecentEvents);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task Reconnected_RaisesOneSeverityRankedCatchupSummary()
    {
        using var fixture = new ViewModelFixture();
        fixture.Client.Snapshot = Snapshot(DateTimeOffset.UtcNow, 0, []);
        var viewModel = fixture.CreateViewModel(CursorSettings(0));
        await viewModel.InitializeAsync();
        var alerts = new ConcurrentQueue<EventViewModel>();
        viewModel.AlertRaised += (_, item) => alerts.Enqueue(item);

        fixture.Client.EmitState(AgentConnectionState.Offline);
        var now = DateTimeOffset.UtcNow;
        fixture.Client.Changes.AddRange([
            Change(1, Event(1, now) with { Severity = DeviceHealth.Warning }),
            Change(2, Event(2, now.AddSeconds(1)) with { Severity = DeviceHealth.Critical })
        ]);
        fixture.Client.Snapshot = Snapshot(now.AddSeconds(2), 2, []);
        fixture.Client.EmitState(AgentConnectionState.Connected);
        await WaitUntilAsync(() => viewModel.AppliedChangeCursor == 2 && alerts.Count == 1);

        var summary = Assert.Single(alerts);
        Assert.Equal(DeviceHealth.Critical, summary.Severity);
        Assert.Contains("놓친 변경 2건", summary.Title, StringComparison.Ordinal);
        Assert.Equal("event-2", summary.NavigationEventId);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task InitialBaselineAndRetentionReset_DoNotRaiseCatchupSummary()
    {
        using var baselineFixture = new ViewModelFixture();
        baselineFixture.Client.Snapshot = Snapshot(DateTimeOffset.UtcNow, 5, []);
        baselineFixture.Client.Changes.Add(Change(5, Event(5, DateTimeOffset.UtcNow) with { Severity = DeviceHealth.Critical }));
        var baseline = baselineFixture.CreateViewModel(new ViewerSettings { DemoMode = true });
        var baselineAlerts = 0;
        baseline.AlertRaised += (_, _) => baselineAlerts++;
        await baseline.InitializeAsync();
        Assert.Equal(0, baselineAlerts);
        await baseline.DisposeAsync();

        using var resetFixture = new ViewModelFixture();
        resetFixture.Client.Snapshot = Snapshot(DateTimeOffset.UtcNow, 250, []);
        resetFixture.Client.Recent = [Event(42, DateTimeOffset.UtcNow) with { Severity = DeviceHealth.Critical }];
        resetFixture.Client.ChangePages.Enqueue(new EventChangePageDto(250, 250, false, [], true, 250));
        var reset = resetFixture.CreateViewModel(CursorSettings(10));
        var resetAlerts = 0;
        reset.AlertRaised += (_, _) => resetAlerts++;
        await reset.InitializeAsync();
        Assert.Equal(0, resetAlerts);
        await reset.DisposeAsync();
    }

    [Fact]
    public async Task StartupHubFailure_PreservesSnapshotAndShowsStaleState()
    {
        using var fixture = new ViewModelFixture();
        fixture.Client.Snapshot = Snapshot(DateTimeOffset.UtcNow, 0, [Device("a", DeviceHealth.Normal, DateTimeOffset.UtcNow)]);
        fixture.Client.StartException = new HttpRequestException("offline");
        var viewModel = fixture.CreateViewModel(CursorSettings(0));

        await viewModel.InitializeAsync();

        Assert.Single(viewModel.Devices);
        Assert.Equal(AgentConnectionState.Stale, viewModel.ConnectionState);
        Assert.Equal(AgentConnectionState.Connected, viewModel.HttpConnectionState);
        Assert.Equal(AgentConnectionState.Offline, viewModel.RealtimeConnectionState);
        Assert.Contains("AGENT_UNREACHABLE", viewModel.OperationMessage, StringComparison.Ordinal);

        viewModel.ApplySnapshot(Snapshot(DateTimeOffset.UtcNow.AddMinutes(1), 0,
            [Device("a", DeviceHealth.Normal, DateTimeOffset.UtcNow)]));
        Assert.Equal(AgentConnectionState.Stale, viewModel.ConnectionState);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task RealtimeReconnect_IsSeparateFromHealthyHttpSnapshot()
    {
        using var fixture = new ViewModelFixture();
        fixture.Client.Snapshot = Snapshot(DateTimeOffset.UtcNow, 0,
            [Device("a", DeviceHealth.Normal, DateTimeOffset.UtcNow)]);
        var viewModel = fixture.CreateViewModel(CursorSettings(0));
        await viewModel.InitializeAsync();

        fixture.Client.EmitState(AgentConnectionState.Reconnecting);

        Assert.Equal(AgentConnectionState.Connected, viewModel.HttpConnectionState);
        Assert.Equal(AgentConnectionState.Reconnecting, viewModel.RealtimeConnectionState);
        Assert.Equal(AgentConnectionState.Reconnecting, viewModel.ConnectionState);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task ConnectionConfigurationFailure_TransitionsToNeedsConnectionWithStableSafeCode()
    {
        using var fixture = new ViewModelFixture();
        fixture.Client.Snapshot = Snapshot(DateTimeOffset.UtcNow, 0, []);
        fixture.Client.StartException = new AgentClientException(
            "VIEWER_CONNECTION_REQUIRED",
            AgentConnectionState.NeedsConnection,
            new HttpRequestException("sensitive endpoint"));
        var viewModel = fixture.CreateViewModel(CursorSettings(0));

        await viewModel.InitializeAsync();

        Assert.Equal(AgentConnectionState.NeedsConnection, viewModel.ConnectionState);
        Assert.Equal(AgentConnectionState.NeedsConnection, viewModel.HttpConnectionState);
        Assert.Equal(AgentConnectionState.NeedsConnection, viewModel.RealtimeConnectionState);
        Assert.Contains("VIEWER_CONNECTION_REQUIRED", viewModel.OperationMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive", viewModel.OperationMessage, StringComparison.OrdinalIgnoreCase);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task StartupChangeFeedFailure_StartsRecoveryLoopAndCanReconnect()
    {
        using var fixture = new ViewModelFixture();
        fixture.Client.Snapshot = Snapshot(DateTimeOffset.UtcNow, 0, []);
        fixture.Client.ChangeExceptionAfterCalls = 0;
        fixture.Client.ChangeException = new HttpRequestException("temporary feed failure");
        var viewModel = fixture.CreateViewModel(CursorSettings(0));

        await viewModel.InitializeAsync();
        Assert.Equal(AgentConnectionState.Stale, viewModel.ConnectionState);

        fixture.Client.ChangeException = null;
        fixture.Client.Changes.Add(Change(1, Event(1, DateTimeOffset.UtcNow)));
        fixture.Client.Snapshot = Snapshot(DateTimeOffset.UtcNow, 1, []);
        fixture.Client.EmitState(AgentConnectionState.Connected);
        await WaitUntilAsync(() => viewModel.AppliedChangeCursor == 1);

        Assert.Single(viewModel.RecentEvents);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task LiveDuplicateChange_RaisesOneAlert()
    {
        using var fixture = new ViewModelFixture();
        fixture.Client.Snapshot = Snapshot(DateTimeOffset.UtcNow, 0, []);
        var viewModel = fixture.CreateViewModel(CursorSettings(0));
        await viewModel.InitializeAsync();
        var alerts = 0;
        viewModel.AlertRaised += (_, _) => alerts++;
        var change = Change(1, Event(1, DateTimeOffset.UtcNow) with { Severity = DeviceHealth.Critical });

        fixture.Client.Emit(change);
        fixture.Client.Emit(change);
        await WaitUntilAsync(() => viewModel.AppliedChangeCursor == 1);

        Assert.Single(viewModel.RecentEvents);
        Assert.Equal(1, alerts);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task LiveRecovery_RaisesOnceWithDurationAndDoesNotIncreaseUnacknowledgedCount()
    {
        using var fixture = new ViewModelFixture();
        fixture.Client.Snapshot = Snapshot(DateTimeOffset.UtcNow, 0, []);
        var viewModel = fixture.CreateViewModel(CursorSettings(0));
        await viewModel.InitializeAsync();
        var alerts = new ConcurrentQueue<EventViewModel>();
        viewModel.AlertRaised += (_, item) => alerts.Enqueue(item);
        var occurred = DateTimeOffset.UtcNow;
        var active = Event(1, occurred) with
        {
            Severity = DeviceHealth.Critical,
            ConditionKey = "uplink:24",
            IsActiveCondition = true
        };

        fixture.Client.Emit(Change(1, active));
        await WaitUntilAsync(() => viewModel.AppliedChangeCursor == 1);
        Assert.Equal(1, viewModel.UnacknowledgedCount);

        var recovered = active with
        {
            Severity = DeviceHealth.Normal,
            Recovered = true,
            Acknowledged = false,
            IsActiveCondition = false,
            RecoveredAt = occurred.AddMinutes(2).AddSeconds(5),
            Title = "업링크 복구"
        };
        fixture.Client.Emit(Change(2, recovered, "Recovered"));
        fixture.Client.Emit(Change(3, recovered, "Recovered"));
        await WaitUntilAsync(() => viewModel.AppliedChangeCursor == 3);

        Assert.Equal(2, alerts.Count);
        var recoveryAlert = alerts.Last();
        Assert.True(recoveryAlert.Recovered);
        Assert.Contains("2분 5초", recoveryAlert.AlertDetail, StringComparison.Ordinal);
        Assert.Equal(0, viewModel.UnacknowledgedCount);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task NavigateToEvent_SelectsItsDeviceAndEvent()
    {
        using var fixture = new ViewModelFixture();
        var now = DateTimeOffset.UtcNow;
        var viewModel = fixture.CreateViewModel();
        viewModel.ApplySnapshot(Snapshot(now, 0,
            [Device("a", DeviceHealth.Normal, now), Device("b", DeviceHealth.Warning, now)]));
        viewModel.ApplyEvents([Event(7, now) with { DeviceId = "b", DeviceName = "SW-B" }], false);

        Assert.True(viewModel.NavigateToEvent("event-7"));
        Assert.Equal("b", viewModel.SelectedDevice?.Id);
        Assert.Equal("event-7", viewModel.SelectedEvent?.AgentEventId);
        Assert.False(viewModel.NavigateToEvent("missing"));
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_CompletesWithinFiveSeconds()
    {
        using var fixture = new ViewModelFixture();
        var viewModel = fixture.CreateViewModel();
        await viewModel.InitializeAsync();
        var stopwatch = Stopwatch.StartNew();

        await viewModel.DisposeAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DisposeAsync_CancelsInitializationThatIsWaitingOnNetwork()
    {
        using var fixture = new ViewModelFixture();
        fixture.Client.BlockSnapshot = true;
        var viewModel = fixture.CreateViewModel();
        var initialization = viewModel.InitializeAsync();
        await fixture.Client.SnapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var stopwatch = Stopwatch.StartNew();

        await viewModel.DisposeAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);
    }

    [Fact]
    public async Task SwitchClient_FailedPreflightKeepsExistingClientAndSettings()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-ViewerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var original = new FakeAgentClient();
            var replacement = new FakeAgentClient { StartException = new HttpRequestException("hub unavailable") };
            var originalSettings = new ViewerSettings { DemoMode = true, AgentUri = "http://original.example.test:18443" };
            var store = new ViewerSettingsStore(Path.Combine(folder, "settings.json"));
            var viewModel = new DashboardViewModel(originalSettings, store, new QueueFactory(original, replacement));
            await viewModel.InitializeAsync();
            var candidate = new ViewerSettings { DemoMode = true, AgentUri = "http://replacement.example.test:18443" };

            await Assert.ThrowsAsync<HttpRequestException>(() => viewModel.SwitchClientAsync(candidate));

            Assert.Equal("http://original.example.test:18443", viewModel.CurrentSettings.AgentUri);
            Assert.False(original.DisposeCalled);
            Assert.True(replacement.DisposeCalled);
            await viewModel.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task SwitchClient_PostSwapCatchupFailureKeepsReplacementAndDisposesPrevious()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-ViewerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var original = new FakeAgentClient();
            var replacement = new FakeAgentClient
            {
                Snapshot = Snapshot(DateTimeOffset.UtcNow, 1, []),
                ChangeExceptionAfterCalls = 1,
                ChangeException = new HttpRequestException("catchup interrupted")
            };
            var originalSettings = new ViewerSettings { DemoMode = true, AgentUri = "http://original.example.test:18443" };
            var store = new ViewerSettingsStore(Path.Combine(folder, "settings.json"));
            var viewModel = new DashboardViewModel(originalSettings, store, new QueueFactory(original, replacement));
            await viewModel.InitializeAsync();
            var candidate = new ViewerSettings { DemoMode = true, AgentUri = "http://replacement.example.test:18443" };

            await viewModel.SwitchClientAsync(candidate);

            Assert.Equal("http://replacement.example.test:18443", viewModel.CurrentSettings.AgentUri);
            Assert.Equal(AgentConnectionState.Stale, viewModel.ConnectionState);
            Assert.True(original.DisposeCalled);
            Assert.False(replacement.DisposeCalled);
            await viewModel.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task SwitchClient_WaitsForInitializationBeforeReplacingClient()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-ViewerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var original = new FakeAgentClient { BlockSnapshot = true };
            var replacement = new FakeAgentClient
            {
                Snapshot = Snapshot(DateTimeOffset.UtcNow, 0,
                    [Device("replacement", DeviceHealth.Normal, DateTimeOffset.UtcNow)])
            };
            var store = new ViewerSettingsStore(Path.Combine(folder, "settings.json"));
            var viewModel = new DashboardViewModel(new ViewerSettings { DemoMode = true }, store,
                new QueueFactory(original, replacement));
            var initialization = viewModel.InitializeAsync();
            await original.SnapshotBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var switching = viewModel.SwitchClientAsync(new ViewerSettings
            {
                DemoMode = true,
                AgentUri = "http://replacement.example.test:18443"
            });
            await Task.Delay(100);
            Assert.False(replacement.SnapshotStarted.Task.IsCompleted);

            original.ReleaseSnapshot.TrySetResult();
            await initialization;
            await switching;

            Assert.Equal("replacement", Assert.Single(viewModel.Devices).Id);
            await viewModel.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task ClientSwap_PreventsBlockedOldRefreshFromOverwritingNewSnapshot()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-ViewerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var original = new FakeAgentClient
            {
                Snapshot = Snapshot(DateTimeOffset.UtcNow, 0,
                    [Device("old", DeviceHealth.Warning, DateTimeOffset.UtcNow)])
            };
            var replacement = new FakeAgentClient
            {
                Snapshot = Snapshot(DateTimeOffset.UtcNow, 0,
                    [Device("new", DeviceHealth.Normal, DateTimeOffset.UtcNow)])
            };
            var store = new ViewerSettingsStore(Path.Combine(folder, "settings.json"));
            var viewModel = new DashboardViewModel(new ViewerSettings { DemoMode = true }, store,
                new QueueFactory(original, replacement));
            await viewModel.InitializeAsync();
            original.BlockSnapshotCall = 2;

            viewModel.RefreshCommand.Execute(null);
            await original.SnapshotBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await viewModel.SwitchClientAsync(new ViewerSettings
            {
                DemoMode = true,
                AgentUri = "http://replacement.example.test:18443"
            });
            original.ReleaseSnapshot.TrySetResult();
            await WaitUntilAsync(() => !viewModel.IsBusy);

            Assert.Equal("new", Assert.Single(viewModel.Devices).Id);
            await viewModel.DisposeAsync();
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task OverlappingRefresh_DoesNotApplyOlderSnapshotAfterNewerOne()
    {
        using var fixture = new ViewModelFixture();
        var baseline = DateTimeOffset.UtcNow;
        fixture.Client.Snapshot = Snapshot(baseline, 0,
            [Device("baseline", DeviceHealth.Normal, baseline)]);
        var viewModel = fixture.CreateViewModel(CursorSettings(0));
        await viewModel.InitializeAsync();

        var older = baseline.AddMinutes(1);
        fixture.Client.Snapshot = Snapshot(older, 0,
            [Device("older", DeviceHealth.Warning, older)]);
        fixture.Client.BlockSnapshotCall = 2;
        viewModel.RefreshCommand.Execute(null);
        await fixture.Client.SnapshotBlocked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var newer = baseline.AddMinutes(2);
        fixture.Client.Snapshot = Snapshot(newer, 0,
            [Device("newer", DeviceHealth.Normal, newer)]);
        fixture.Client.EmitState(AgentConnectionState.Connected);
        await WaitUntilAsync(() => viewModel.Devices.Count == 1 && viewModel.Devices[0].Id == "newer");
        fixture.Client.ReleaseSnapshot.TrySetResult();
        await WaitUntilAsync(() => !viewModel.IsBusy);

        Assert.Equal("newer", Assert.Single(viewModel.Devices).Id);
        await viewModel.DisposeAsync();
    }

    private static ViewerSettings CursorSettings(long cursor)
    {
        var settings = new ViewerSettings { DemoMode = true };
        settings.SetEventCursor("fake-agent", cursor);
        return settings;
    }

    private static AgentSnapshotDto Snapshot(DateTimeOffset now, long highWatermark, IReadOnlyList<DeviceSnapshotDto> devices) =>
        new(now, AgentConnectionState.Connected, devices, highWatermark, "test", "test", "fake-agent");

    private static DeviceSnapshotDto Device(string id, DeviceHealth health, DateTimeOffset now) =>
        new(id, "SW-" + id.ToUpperInvariant(), "IES4224GP", "비공개", health, now, "summary", "1일", []);

    private static SwitchEventDto Event(long sequence, DateTimeOffset when) =>
        new(sequence, $"event-{sequence}", "a", "SW-A", when, DeviceHealth.Warning, "새 로그", "이벤트", "detail");

    private static AgentEventChangeDto Change(long sequence, SwitchEventDto item, string kind = "Created") =>
        new(sequence, kind, item);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition(), "Condition was not reached before timeout.");
    }

    private sealed class ViewModelFixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-ViewerTests", Guid.NewGuid().ToString("N"));
        public FakeAgentClient Client { get; } = new();

        public DashboardViewModel CreateViewModel(ViewerSettings? settings = null)
        {
            Directory.CreateDirectory(_folder);
            var store = new ViewerSettingsStore(Path.Combine(_folder, "settings.json"));
            return new DashboardViewModel(settings ?? new ViewerSettings { DemoMode = true }, store, new FakeFactory(Client));
        }

        public void Dispose()
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }
    }

    private sealed class FakeFactory(FakeAgentClient client) : IAgentClientFactory
    {
        public IAgentClient Create(ViewerSettings settings) => client;
    }

    private sealed class QueueFactory(params IAgentClient[] clients) : IAgentClientFactory
    {
        private readonly Queue<IAgentClient> _clients = new(clients);
        public IAgentClient Create(ViewerSettings settings) => _clients.Dequeue();
    }

    private sealed class FakeAgentClient : IAgentClient
    {
        public AgentSnapshotDto Snapshot { get; set; } = DashboardViewModelTests.Snapshot(DateTimeOffset.UtcNow, 0, []);
        public IReadOnlyList<SwitchEventDto> Recent { get; set; } = [];
        public List<AgentEventChangeDto> Changes { get; } = [];
        public Queue<EventChangePageDto> ChangePages { get; } = new();
        public Exception? StartException { get; set; }
        public Exception? ChangeException { get; set; }
        public int ChangeExceptionAfterCalls { get; set; } = int.MaxValue;
        public bool BlockSnapshot { get; set; }
        public int BlockSnapshotCall { get; set; } = int.MaxValue;
        private int _snapshotCalls;
        public TaskCompletionSource SnapshotStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SnapshotBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSnapshot { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool StartCalled { get; private set; }
        public int ChangePageCalls { get; private set; }
        private int _recentCalls;
        public int RecentCalls => Volatile.Read(ref _recentCalls);
        public int BlockRecentAfterCalls { get; set; } = int.MaxValue;
        public TaskCompletionSource RecentBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRecent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool DisposeCalled { get; private set; }
        public event EventHandler<AgentEventChangeDto>? EventChanged;
        public event EventHandler<AgentConnectionState>? ConnectionStateChanged;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCalled = true;
            if (StartException is not null)
            {
                ConnectionStateChanged?.Invoke(this, AgentConnectionState.Offline);
                return Task.FromException(StartException);
            }
            ConnectionStateChanged?.Invoke(this, Snapshot.ConnectionState);
            return Task.CompletedTask;
        }

        public async Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            SnapshotStarted.TrySetResult();
            var result = Snapshot;
            var call = Interlocked.Increment(ref _snapshotCalls);
            if (BlockSnapshot || call == BlockSnapshotCall)
            {
                SnapshotBlocked.TrySetResult();
                await ReleaseSnapshot.Task.WaitAsync(cancellationToken);
            }
            return result;
        }

        public async Task<IReadOnlyList<SwitchEventDto>> GetRecentEventsAsync(int limit, CancellationToken cancellationToken)
        {
            var result = Recent.Take(limit).ToArray();
            var call = Interlocked.Increment(ref _recentCalls);
            if (call > BlockRecentAfterCalls)
            {
                RecentBlocked.TrySetResult();
                await ReleaseRecent.Task.WaitAsync(cancellationToken);
            }
            return result;
        }

        public Task<EventChangePageDto> GetEventChangesAsync(long cursor, int limit, CancellationToken cancellationToken)
        {
            ChangePageCalls++;
            if (ChangePageCalls > ChangeExceptionAfterCalls && ChangeException is not null)
            {
                return Task.FromException<EventChangePageDto>(ChangeException);
            }
            if (ChangePages.TryDequeue(out var configuredPage))
            {
                return Task.FromResult(configuredPage);
            }
            AgentEventChangeDto[] items;
            lock (Changes)
            {
                items = Changes.Where(item => item.ChangeSequence > cursor)
                    .OrderBy(item => item.ChangeSequence)
                    .Take(limit)
                    .ToArray();
            }
            var high = Math.Max(Snapshot.HighWatermark, Changes.Count == 0 ? 0 : Changes.Max(item => item.ChangeSequence));
            var next = items.Length == 0 ? cursor : items[^1].ChangeSequence;
            return Task.FromResult(new EventChangePageDto(high, next, next < high, items));
        }

        public Task<CommandResultDto> ExecuteRegisteredCheckAsync(string deviceId, string commandId, CancellationToken cancellationToken) =>
            Task.FromResult(new CommandResultDto(true, "ok"));

        public Task<bool> AcknowledgeAsync(string eventId, CancellationToken cancellationToken) => Task.FromResult(true);

        public void Emit(AgentEventChangeDto item)
        {
            lock (Changes)
            {
                if (Changes.All(change => change.ChangeSequence != item.ChangeSequence)) Changes.Add(item);
            }
            EventChanged?.Invoke(this, item);
        }

        public void EmitState(AgentConnectionState state) => ConnectionStateChanged?.Invoke(this, state);
        public ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            return ValueTask.CompletedTask;
        }
    }
}
