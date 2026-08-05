using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerFailureReportingTests
{
    [Fact]
    public async Task AgentConnectionRefused_RecoveryClearsStaleMessageAndWritesOneTransitionPair()
    {
        var folder = TemporaryFolder();
        var diagnosticLog = new ViewerDiagnosticLog(
            Path.Combine(folder, "logs"),
            applicationVersion: "1.2.3-poc");
        var settingsStore = new ViewerSettingsStore(
            Path.Combine(folder, "settings.json"));
        var client = new TestAgentClient(supportsStatelessV4: true)
        {
            StartException = new AgentClientException(
                "AGENT_CONNECTION_REFUSED",
                AgentConnectionState.Offline,
                new HttpRequestException(
                    "host=192.0.2.10 user=operator password=login-secret"))
        };
        var viewModel = new DashboardViewModel(
            new ViewerSettings
            {
                DemoMode = false,
                AgentUri = "https://agent.example.test:18443"
            },
            settingsStore,
            new TestFactory(client),
            synchronizationContext: null,
            deviceStore: null,
            monitoringStore: null,
            settingsSaveCoordinator: new ViewerSettingsSaveCoordinator(
                settingsStore,
                diagnosticLog.Write),
            writeDiagnostic: diagnosticLog.Write,
            settingsSaveDelay: static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken),
            writeConnectionDiagnostic: diagnosticLog.WriteConnectionTransition);
        try
        {
            await viewModel.InitializeAsync();

            Assert.Equal(AgentConnectionState.Offline, viewModel.ConnectionState);
            Assert.Contains(
                "AGENT_CONNECTION_REFUSED",
                viewModel.OperationMessage,
                StringComparison.Ordinal);

            client.EmitState(AgentConnectionState.Connected);
            client.EmitState(AgentConnectionState.Connected);

            Assert.Equal(AgentConnectionState.Connected, viewModel.ConnectionState);
            Assert.DoesNotContain(
                "AGENT_CONNECTION_REFUSED",
                viewModel.OperationMessage,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Agent 상태 미확인",
                viewModel.OperationMessage,
                StringComparison.Ordinal);

            var content = File.ReadAllText(diagnosticLog.CurrentPath);
            Assert.DoesNotContain("192.0.2.10", content, StringComparison.Ordinal);
            Assert.DoesNotContain("operator", content, StringComparison.Ordinal);
            Assert.DoesNotContain("login-secret", content, StringComparison.Ordinal);
            var lines = File.ReadAllLines(diagnosticLog.CurrentPath);
            Assert.Equal(2, lines.Length);
            using var failed = JsonDocument.Parse(lines[0]);
            using var recovered = JsonDocument.Parse(lines[1]);
            Assert.Equal(
                "AGENT_CONNECTION_REFUSED",
                failed.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(
                "failed",
                failed.RootElement.GetProperty("transition").GetString());
            Assert.Equal(
                "AGENT_CONNECTION_REFUSED",
                recovered.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(
                "recovered",
                recovered.RootElement.GetProperty("transition").GetString());
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task DemoState_DoesNotRecordAgentRecovery()
    {
        var folder = TemporaryFolder();
        var diagnosticLog = new ViewerDiagnosticLog(
            Path.Combine(folder, "logs"),
            applicationVersion: "1.2.3-poc");
        var settingsStore = new ViewerSettingsStore(
            Path.Combine(folder, "settings.json"));
        var client = new TestAgentClient(supportsStatelessV4: true)
        {
            StartException = new AgentClientException(
                "AGENT_CONNECTION_REFUSED",
                AgentConnectionState.Offline)
        };
        var viewModel = new DashboardViewModel(
            new ViewerSettings { DemoMode = true },
            settingsStore,
            new TestFactory(client),
            synchronizationContext: null,
            deviceStore: null,
            monitoringStore: null,
            settingsSaveCoordinator: new ViewerSettingsSaveCoordinator(
                settingsStore,
                diagnosticLog.Write),
            writeDiagnostic: diagnosticLog.Write,
            settingsSaveDelay: static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken),
            writeConnectionDiagnostic: diagnosticLog.WriteConnectionTransition);
        try
        {
            await viewModel.InitializeAsync();
            client.EmitState(AgentConnectionState.Demo);

            Assert.Equal(AgentConnectionState.Demo, viewModel.ConnectionState);
            var transition = Assert.Single(ReadConnectionTransitions(
                diagnosticLog.CurrentPath));
            Assert.Equal(
                "AGENT_CONNECTION_REFUSED",
                transition.GetProperty("errorCode").GetString());
            Assert.Equal(
                "failed",
                transition.GetProperty("transition").GetString());
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task ConnectionSwitch_PreflightFailureWritesSafeStableDiagnostic()
    {
        var folder = TemporaryFolder();
        var diagnosticLog = new ViewerDiagnosticLog(
            Path.Combine(folder, "logs"),
            applicationVersion: "1.2.3-poc");
        var settingsStore = new ViewerSettingsStore(
            Path.Combine(folder, "settings.json"));
        var original = new TestAgentClient(supportsStatelessV4: true);
        var replacement = new TestAgentClient(supportsStatelessV4: true)
        {
            StartException = new AgentClientException(
                "AGENT_CONNECTION_REFUSED",
                AgentConnectionState.Offline,
                new HttpRequestException(
                    "host=192.0.2.10 user=operator password=login-secret"))
        };
        var viewModel = new DashboardViewModel(
            new ViewerSettings { DemoMode = true },
            settingsStore,
            new QueueFactory(original, replacement),
            synchronizationContext: null,
            deviceStore: null,
            monitoringStore: null,
            settingsSaveCoordinator: new ViewerSettingsSaveCoordinator(
                settingsStore,
                diagnosticLog.Write),
            writeDiagnostic: diagnosticLog.Write,
            settingsSaveDelay: static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken),
            writeConnectionDiagnostic: diagnosticLog.WriteConnectionTransition);
        try
        {
            await viewModel.InitializeAsync();

            var failure = await Assert.ThrowsAsync<AgentClientException>(() =>
                viewModel.SwitchClientAsync(new ViewerSettings
                {
                    DemoMode = false,
                    AgentUri = "https://replacement.example.test:18443"
                }));

            Assert.Equal("AGENT_CONNECTION_REFUSED", failure.ErrorCode);
            var content = File.ReadAllText(diagnosticLog.CurrentPath);
            Assert.DoesNotContain("192.0.2.10", content, StringComparison.Ordinal);
            Assert.DoesNotContain("operator", content, StringComparison.Ordinal);
            Assert.DoesNotContain("login-secret", content, StringComparison.Ordinal);
            var transition = Assert.Single(ReadConnectionTransitions(
                diagnosticLog.CurrentPath));
            Assert.Equal(
                "agent-http",
                transition.GetProperty("stage").GetString());
            Assert.Equal(
                "AGENT_CONNECTION_REFUSED",
                transition.GetProperty("errorCode").GetString());
            Assert.Equal(
                "failed",
                transition.GetProperty("transition").GetString());
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task MonitoringCycle_StateWriteFailureKeepsStorageCode()
    {
        await AssertMonitoringCycleFailureAsync(
            new IOException("host=192.0.2.10 user=operator password=login-secret"),
            "VIEWER_MONITOR_STATE_WRITE_FAILED");
    }

    [Fact]
    public async Task MonitoringCycle_UnexpectedFailureUsesCycleCode()
    {
        await AssertMonitoringCycleFailureAsync(
            new InvalidOperationException(
                "host=192.0.2.10 user=operator password=login-secret"),
            "VIEWER_MONITOR_CYCLE_FAILED");
    }

    [Fact]
    public async Task MonitoringCycle_AbandonedUiPostTimesOutAndCanRunAgain()
    {
        var folder = TemporaryFolder();
        var uiContext = new SwitchableSynchronizationContext();
        var monitoringPersistence = new TestMonitoringPersistence();
        var diagnosticLog = new ViewerDiagnosticLog(Path.Combine(folder, "logs"));
        var settingsStore = new ViewerSettingsStore(Path.Combine(folder, "settings.json"));
        var viewModel = new DashboardViewModel(
            new ViewerSettings { DemoMode = true },
            settingsStore,
            new TestFactory(new TestAgentClient(supportsStatelessV4: true)),
            synchronizationContext: uiContext,
            deviceStore: new ManagedDeviceStore(
                Path.Combine(folder, "devices.json"),
                new TestProtector()),
            monitoringStore: new ViewerMonitoringStore(
                Path.Combine(folder, "monitor.json"),
                monitoringPersistence),
            settingsSaveCoordinator: new ViewerSettingsSaveCoordinator(
                settingsStore,
                diagnosticLog.Write),
            writeDiagnostic: diagnosticLog.Write,
            settingsSaveDelay: static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken));
        try
        {
            await viewModel.InitializeAsync();
            await WaitUntilAsync(() => monitoringPersistence.WriteCount >= 2);
            await viewModel.RunMonitoringCycleAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
            monitoringPersistence.WriteException = new IOException("private path");
            uiContext.DropPosts = true;

            await viewModel.RunMonitoringCycleSafelyAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));

            AssertDiagnostic(
                diagnosticLog.CurrentPath,
                "monitoring-cycle",
                "VIEWER_MONITOR_STATE_WRITE_FAILED");

            uiContext.DropPosts = false;
            await viewModel.RunMonitoringCycleSafelyAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Contains(
                "VIEWER_MONITOR_STATE_WRITE_FAILED",
                viewModel.OperationMessage,
                StringComparison.Ordinal);
        }
        finally
        {
            uiContext.DropPosts = false;
            monitoringPersistence.WriteException = null;
            await viewModel.DisposeAsync();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task StatelessInitialization_SettingsSaveFailureKeepsAgentConnected()
    {
        var folder = TemporaryFolder();
        var settingsPersistence = new TestSettingsPersistence
        {
            WriteException = new UnauthorizedAccessException("private path")
        };
        var diagnosticLog = new ViewerDiagnosticLog(Path.Combine(folder, "logs"));
        var settingsStore = new ViewerSettingsStore(
            Path.Combine(folder, "settings.json"),
            settingsPersistence);
        var coordinator = new ViewerSettingsSaveCoordinator(
            settingsStore,
            diagnosticLog.Write);
        var viewModel = new DashboardViewModel(
            new ViewerSettings { DemoMode = true },
            settingsStore,
            new TestFactory(new TestAgentClient(supportsStatelessV4: true)),
            synchronizationContext: null,
            deviceStore: null,
            monitoringStore: null,
            settingsSaveCoordinator: coordinator,
            writeDiagnostic: diagnosticLog.Write,
            settingsSaveDelay: static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken));
        try
        {
            await viewModel.InitializeAsync();

            Assert.Equal(AgentConnectionState.Demo, viewModel.ConnectionState);
            Assert.Contains("VIEWER_SETTINGS_WRITE_FAILED", viewModel.OperationMessage, StringComparison.Ordinal);
            AssertDiagnostic(
                diagnosticLog.CurrentPath,
                "settings-save-connection",
                "VIEWER_SETTINGS_WRITE_FAILED");
            Assert.Empty(ReadConnectionTransitions(diagnosticLog.CurrentPath));
        }
        finally
        {
            settingsPersistence.WriteException = null;
            await viewModel.DisposeAsync();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task ConnectionSettings_SaveFailureKeepsExistingClientAndSettings()
    {
        var folder = TemporaryFolder();
        var settingsPersistence = new TestSettingsPersistence();
        var diagnosticLog = new ViewerDiagnosticLog(Path.Combine(folder, "logs"));
        var settingsStore = new ViewerSettingsStore(
            Path.Combine(folder, "settings.json"),
            settingsPersistence);
        var coordinator = new ViewerSettingsSaveCoordinator(
            settingsStore,
            diagnosticLog.Write);
        var original = new TestAgentClient(supportsStatelessV4: true);
        var replacement = new TestAgentClient(supportsStatelessV4: true);
        var originalSettings = new ViewerSettings
        {
            DemoMode = true,
            AgentUri = "https://original.example.test:18443"
        };
        var viewModel = new DashboardViewModel(
            originalSettings,
            settingsStore,
            new QueueFactory(original, replacement),
            synchronizationContext: null,
            deviceStore: null,
            monitoringStore: null,
            settingsSaveCoordinator: coordinator,
            writeDiagnostic: diagnosticLog.Write,
            settingsSaveDelay: static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken));
        try
        {
            await viewModel.InitializeAsync();
            settingsPersistence.WriteException =
                new IOException("private settings path");

            var failure = await Assert.ThrowsAsync<AgentClientException>(() =>
                viewModel.SwitchClientAsync(new ViewerSettings
                {
                    DemoMode = true,
                    AgentUri = "https://replacement.example.test:18443"
                }));

            Assert.Equal("VIEWER_SETTINGS_WRITE_FAILED", failure.ErrorCode);
            Assert.Equal(
                "https://original.example.test:18443",
                viewModel.CurrentSettings.AgentUri);
            Assert.Equal(
                "https://original.example.test:18443",
                settingsStore.Load().AgentUri);
            Assert.False(original.DisposeCalled);
            Assert.True(replacement.DisposeCalled);
            AssertDiagnostic(
                diagnosticLog.CurrentPath,
                "settings-save-connection",
                "VIEWER_SETTINGS_WRITE_FAILED");
            Assert.Empty(ReadConnectionTransitions(diagnosticLog.CurrentPath));
        }
        finally
        {
            settingsPersistence.WriteException = null;
            await viewModel.DisposeAsync();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task ScheduledSettingsSave_FailureIsVisibleAndKeepsAgentState()
    {
        var folder = TemporaryFolder();
        var releaseSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var settingsPersistence = new TestSettingsPersistence
        {
            WriteException = new IOException("private event cursor")
        };
        var diagnosticLog = new ViewerDiagnosticLog(Path.Combine(folder, "logs"));
        var settingsStore = new ViewerSettingsStore(
            Path.Combine(folder, "settings.json"),
            settingsPersistence);
        var coordinator = new ViewerSettingsSaveCoordinator(
            settingsStore,
            diagnosticLog.Write);
        var viewModel = new DashboardViewModel(
            new ViewerSettings { DemoMode = true },
            settingsStore,
            new TestFactory(new TestAgentClient(supportsStatelessV4: false)),
            synchronizationContext: null,
            deviceStore: null,
            monitoringStore: null,
            settingsSaveCoordinator: coordinator,
            writeDiagnostic: diagnosticLog.Write,
            settingsSaveDelay: (_, cancellationToken) =>
                releaseSave.Task.WaitAsync(cancellationToken));
        try
        {
            await viewModel.InitializeAsync();
            releaseSave.SetResult();
            await WaitUntilAsync(() =>
                viewModel.OperationMessage.Contains(
                    "VIEWER_SETTINGS_WRITE_FAILED",
                    StringComparison.Ordinal));

            Assert.Equal(AgentConnectionState.Connected, viewModel.ConnectionState);
            Assert.True(viewModel.CurrentSettings.TryGetEventCursor("fake-agent", out var cursor));
            Assert.Equal(7, cursor);
            AssertDiagnostic(
                diagnosticLog.CurrentPath,
                "settings-save-background",
                "VIEWER_SETTINGS_WRITE_FAILED");
        }
        finally
        {
            settingsPersistence.WriteException = null;
            releaseSave.TrySetResult();
            await viewModel.DisposeAsync();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task ConnectionSwitch_MergesLatestRuntimeSettingsBeforePersistingCandidate()
    {
        var folder = TemporaryFolder();
        var settingsPersistence = new TestSettingsPersistence();
        var settingsStore = new ViewerSettingsStore(
            Path.Combine(folder, "settings.json"),
            settingsPersistence);
        var original = new TestAgentClient(supportsStatelessV4: true);
        var replacementStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplacement = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = new TestAgentClient(
            supportsStatelessV4: true,
            replacementStart,
            releaseReplacement.Task);
        var originalSettings = new ViewerSettings
        {
            DemoMode = true,
            AgentUri = "https://original.example.test:18443",
            MiniLeft = 10,
            MiniTop = 20
        };
        var originalPin = new string('A', 64);
        var latestOriginalPin = new string('B', 64);
        var replacementPin = new string('C', 64);
        originalSettings.SetAgentTrustPin(originalPin);
        var viewModel = new DashboardViewModel(
            originalSettings,
            settingsStore,
            new QueueFactory(original, replacement),
            synchronizationContext: null,
            deviceStore: null,
            monitoringStore: null);
        try
        {
            await viewModel.InitializeAsync();
            var staleCandidate = viewModel.CurrentSettings;
            var originalCursorKey = staleCandidate.BuildAgentIdentity("fake");
            staleCandidate.DemoMode = false;
            staleCandidate.AgentUri = "https://replacement.example.test:18443";
            staleCandidate.StartMinimizedToTray = true;
            staleCandidate.SetAgentTrustPin(replacementPin);

            var switching = viewModel.SwitchClientAsync(staleCandidate);
            await replacementStart.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(viewModel.TryUpdateCurrentSettings(
                settings =>
                {
                    settings.MiniLeft = 310;
                    settings.MiniTop = 420;
                    settings.MiniTopmost = false;
                    settings.SetEventCursor("fake", 99);
                    settings.SetAgentTrustPin(latestOriginalPin);
                },
                "settings-save-interactive",
                out var updateErrorCode));
            Assert.Empty(updateErrorCode);

            releaseReplacement.SetResult();
            await switching.WaitAsync(TimeSpan.FromSeconds(2));

            var current = viewModel.CurrentSettings;
            var persisted = settingsStore.Load();
            Assert.Equal("https://replacement.example.test:18443", current.AgentUri);
            Assert.True(current.StartMinimizedToTray);
            Assert.Equal(310, current.MiniLeft);
            Assert.Equal(420, current.MiniTop);
            Assert.False(current.MiniTopmost);
            Assert.Equal(99, current.EventCursors[originalCursorKey]);
            Assert.Equal(99, current.LastEventSequence);
            Assert.Equal(
                latestOriginalPin,
                current.AgentTrustPins["HTTPS://ORIGINAL.EXAMPLE.TEST:18443"]);
            Assert.Equal(
                replacementPin,
                current.AgentTrustPins["HTTPS://REPLACEMENT.EXAMPLE.TEST:18443"]);
            Assert.Equal(current.AgentUri, persisted.AgentUri);
            Assert.Equal(current.StartMinimizedToTray, persisted.StartMinimizedToTray);
            Assert.Equal(current.MiniLeft, persisted.MiniLeft);
            Assert.Equal(current.MiniTop, persisted.MiniTop);
            Assert.Equal(current.MiniTopmost, persisted.MiniTopmost);
            Assert.Equal(99, persisted.EventCursors[originalCursorKey]);
            Assert.Equal(99, persisted.LastEventSequence);
            Assert.Equal(
                latestOriginalPin,
                persisted.AgentTrustPins["HTTPS://ORIGINAL.EXAMPLE.TEST:18443"]);
            Assert.Equal(
                replacementPin,
                persisted.AgentTrustPins["HTTPS://REPLACEMENT.EXAMPLE.TEST:18443"]);
        }
        finally
        {
            releaseReplacement.TrySetResult();
            await viewModel.DisposeAsync();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task LegacyConnectionSwitch_PreservesAuthoritativeLowerCursorAfterServerReset()
    {
        var folder = TemporaryFolder();
        var settingsStore = new ViewerSettingsStore(Path.Combine(folder, "settings.json"));
        var original = new TestAgentClient(supportsStatelessV4: false);
        var replacementStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplacement = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = new TestAgentClient(
            supportsStatelessV4: false,
            replacementStart,
            releaseReplacement.Task);
        var originalSettings = new ViewerSettings
        {
            DemoMode = true,
            AgentUri = "https://same-agent.example.test:18443"
        };
        originalSettings.SetEventCursor("fake-agent", 500);
        var viewModel = new DashboardViewModel(
            originalSettings,
            settingsStore,
            new QueueFactory(original, replacement),
            synchronizationContext: null,
            deviceStore: null,
            monitoringStore: null);
        try
        {
            await viewModel.InitializeAsync();
            var staleCandidate = viewModel.CurrentSettings;

            var switching = viewModel.SwitchClientAsync(staleCandidate);
            await replacementStart.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(viewModel.TryUpdateCurrentSettings(
                settings => settings.SetEventCursor("fake-agent", 120),
                "settings-save-interactive",
                out var updateErrorCode));
            Assert.Empty(updateErrorCode);

            releaseReplacement.SetResult();
            await switching.WaitAsync(TimeSpan.FromSeconds(2));

            var current = viewModel.CurrentSettings;
            var persisted = settingsStore.Load();
            Assert.True(current.TryGetEventCursor("fake-agent", out var currentCursor));
            Assert.True(persisted.TryGetEventCursor("fake-agent", out var persistedCursor));
            Assert.Equal(120, currentCursor);
            Assert.Equal(120, persistedCursor);
            Assert.Equal(120, current.LastEventSequence);
            Assert.Equal(120, persisted.LastEventSequence);
            Assert.Equal(120, viewModel.AppliedChangeCursor);
            Assert.Equal(120, replacement.FirstChangeRequestCursor);
        }
        finally
        {
            releaseReplacement.TrySetResult();
            await viewModel.DisposeAsync();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task ConnectionSwitch_DoesNotRestoreStaleCursorKeysAtCapacity()
    {
        var folder = TemporaryFolder();
        var settingsStore = new ViewerSettingsStore(Path.Combine(folder, "settings.json"));
        var original = new TestAgentClient(supportsStatelessV4: false);
        var replacementStart = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReplacement = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var replacement = new TestAgentClient(
            supportsStatelessV4: false,
            replacementStart,
            releaseReplacement.Task);
        var viewModel = new DashboardViewModel(
            new ViewerSettings
            {
                DemoMode = true,
                AgentUri = "https://original.example.test:18443"
            },
            settingsStore,
            new QueueFactory(original, replacement),
            synchronizationContext: null,
            deviceStore: null,
            monitoringStore: null);
        try
        {
            await viewModel.InitializeAsync();
            var staleCandidate = viewModel.CurrentSettings;
            staleCandidate.AgentUri = "https://replacement.example.test:18443";
            var targetCursorKey = staleCandidate.BuildAgentIdentity("fake-agent");
            staleCandidate.EventCursors = Enumerable.Range(0, 32)
                .ToDictionary(index => $"stale-{index}", index => (long)index, StringComparer.Ordinal);

            var switching = viewModel.SwitchClientAsync(staleCandidate);
            await replacementStart.Task.WaitAsync(TimeSpan.FromSeconds(2));

            var latestCursors = Enumerable.Range(0, 32)
                .ToDictionary(index => $"latest-{index}", index => (long)(100 + index), StringComparer.Ordinal);
            Assert.True(viewModel.TryUpdateCurrentSettings(
                settings =>
                {
                    settings.EventCursors = new Dictionary<string, long>(
                        latestCursors,
                        StringComparer.Ordinal);
                    settings.LastEventSequence = 131;
                },
                "settings-save-interactive",
                out var updateErrorCode));
            Assert.Empty(updateErrorCode);

            releaseReplacement.SetResult();
            await switching.WaitAsync(TimeSpan.FromSeconds(2));

            var current = viewModel.CurrentSettings;
            var persisted = settingsStore.Load();
            Assert.Equal(32, current.EventCursors.Count);
            Assert.Equal(32, persisted.EventCursors.Count);
            Assert.Equal(7, current.EventCursors[targetCursorKey]);
            Assert.Equal(7, persisted.EventCursors[targetCursorKey]);
            var preservedLatestKeys = latestCursors.Keys
                .Where(current.EventCursors.ContainsKey)
                .ToArray();
            Assert.Equal(31, preservedLatestKeys.Length);
            Assert.Equal(
                preservedLatestKeys.OrderBy(key => key, StringComparer.Ordinal),
                latestCursors.Keys.Where(persisted.EventCursors.ContainsKey)
                    .OrderBy(key => key, StringComparer.Ordinal));
            Assert.All(preservedLatestKeys, key =>
            {
                Assert.Equal(latestCursors[key], current.EventCursors[key]);
                Assert.Equal(latestCursors[key], persisted.EventCursors[key]);
            });
            Assert.DoesNotContain(current.EventCursors.Keys, key => key.StartsWith("stale-", StringComparison.Ordinal));
            Assert.DoesNotContain(persisted.EventCursors.Keys, key => key.StartsWith("stale-", StringComparison.Ordinal));
            Assert.Equal(7, current.LastEventSequence);
            Assert.Equal(7, persisted.LastEventSequence);
        }
        finally
        {
            releaseReplacement.TrySetResult();
            await viewModel.DisposeAsync();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task LegacyConnectionSwitch_NewBaselineDoesNotRaiseCatchupSummary()
    {
        var folder = TemporaryFolder();
        var settingsStore = new ViewerSettingsStore(Path.Combine(folder, "settings.json"));
        var original = new TestAgentClient(supportsStatelessV4: false);
        var replacement = new TestAgentClient(supportsStatelessV4: false);
        replacement.ChangePages.Enqueue(new EventChangePageDto(7, 7, false, []));
        replacement.ChangePages.Enqueue(new EventChangePageDto(
            8,
            8,
            false,
            [
                new AgentEventChangeDto(
                    8,
                    "Created",
                    new SwitchEventDto(
                        8,
                        "event-8",
                        "switch-a",
                        "SW-A",
                        DateTimeOffset.UtcNow,
                        DeviceHealth.Critical,
                        "system",
                        "Uplink down",
                        "Port 24 is down"))
            ]));
        var viewModel = new DashboardViewModel(
            new ViewerSettings
            {
                DemoMode = true,
                AgentUri = "https://original.example.test:18443"
            },
            settingsStore,
            new QueueFactory(original, replacement),
            synchronizationContext: null,
            deviceStore: null,
            monitoringStore: null,
            settingsSaveCoordinator: new ViewerSettingsSaveCoordinator(settingsStore),
            writeDiagnostic: null,
            settingsSaveDelay: static (_, _) => Task.CompletedTask);
        var alerts = 0;
        viewModel.AlertRaised += (_, _) => Interlocked.Increment(ref alerts);
        try
        {
            await viewModel.InitializeAsync();
            var candidate = viewModel.CurrentSettings;
            candidate.AgentUri = "https://new-agent.example.test:18443";

            await viewModel.SwitchClientAsync(candidate)
                .WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() =>
                settingsStore.Load().TryGetEventCursor("fake-agent", out var cursor)
                && cursor == 8);

            Assert.Equal(0, Volatile.Read(ref alerts));
            Assert.Equal(8, viewModel.AppliedChangeCursor);
            Assert.Contains(viewModel.RecentEvents, item => item.AgentEventId == "event-8");
            var current = viewModel.CurrentSettings;
            var persisted = settingsStore.Load();
            Assert.True(current.TryGetEventCursor("fake-agent", out var currentCursor));
            Assert.True(persisted.TryGetEventCursor("fake-agent", out var persistedCursor));
            Assert.Equal(8, currentCursor);
            Assert.Equal(8, persistedCursor);
            Assert.Equal(8, current.LastEventSequence);
            Assert.Equal(8, persisted.LastEventSequence);
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task CurrentSettings_ReturnsDefensiveSnapshot()
    {
        var folder = TemporaryFolder();
        var settingsStore = new ViewerSettingsStore(Path.Combine(folder, "settings.json"));
        var viewModel = new DashboardViewModel(
            new ViewerSettings
            {
                DemoMode = true,
                AgentUri = "https://original.example.test:18443",
                MiniTopmost = true
            },
            settingsStore,
            new TestFactory(new TestAgentClient(supportsStatelessV4: true)));
        try
        {
            var snapshot = viewModel.CurrentSettings;
            snapshot.AgentUri = "https://mutated.example.test:18443";
            snapshot.MiniTopmost = false;
            snapshot.SetEventCursor("fake", 50);

            var current = viewModel.CurrentSettings;
            Assert.Equal("https://original.example.test:18443", current.AgentUri);
            Assert.True(current.MiniTopmost);
            Assert.False(current.TryGetEventCursor("fake", out _));
        }
        finally
        {
            await viewModel.DisposeAsync();
            Directory.Delete(folder, true);
        }
    }

    private static async Task AssertMonitoringCycleFailureAsync(
        Exception failure,
        string expectedCode)
    {
        var folder = TemporaryFolder();
        var monitoringPersistence = new TestMonitoringPersistence();
        var diagnosticLog = new ViewerDiagnosticLog(Path.Combine(folder, "logs"));
        var settingsStore = new ViewerSettingsStore(Path.Combine(folder, "settings.json"));
        var coordinator = new ViewerSettingsSaveCoordinator(
            settingsStore,
            diagnosticLog.Write);
        var viewModel = new DashboardViewModel(
            new ViewerSettings { DemoMode = true },
            settingsStore,
            new TestFactory(new TestAgentClient(supportsStatelessV4: true)),
            synchronizationContext: null,
            deviceStore: new ManagedDeviceStore(
                Path.Combine(folder, "devices.json"),
                new TestProtector()),
            monitoringStore: new ViewerMonitoringStore(
                Path.Combine(folder, "monitor.json"),
                monitoringPersistence),
            settingsSaveCoordinator: coordinator,
            writeDiagnostic: diagnosticLog.Write,
            settingsSaveDelay: static (delay, cancellationToken) =>
                Task.Delay(delay, cancellationToken));
        try
        {
            await viewModel.InitializeAsync();
            await WaitUntilAsync(() => monitoringPersistence.WriteCount >= 2);

            monitoringPersistence.WriteException = failure;
            await viewModel.RunMonitoringCycleSafelyAsync(CancellationToken.None);

            Assert.Equal(AgentConnectionState.Demo, viewModel.ConnectionState);
            Assert.Contains(expectedCode, viewModel.OperationMessage, StringComparison.Ordinal);
            Assert.DoesNotContain(
                expectedCode == "VIEWER_MONITOR_CYCLE_FAILED"
                    ? "VIEWER_MONITOR_STATE_WRITE_FAILED"
                    : "VIEWER_MONITOR_CYCLE_FAILED",
                viewModel.OperationMessage,
                StringComparison.Ordinal);
            AssertDiagnostic(
                diagnosticLog.CurrentPath,
                "monitoring-cycle",
                expectedCode);

            var diagnosticContent = File.ReadAllText(diagnosticLog.CurrentPath);
            Assert.DoesNotContain("192.0.2.10", diagnosticContent, StringComparison.Ordinal);
            Assert.DoesNotContain("operator", diagnosticContent, StringComparison.Ordinal);
            Assert.DoesNotContain("login-secret", diagnosticContent, StringComparison.Ordinal);
        }
        finally
        {
            monitoringPersistence.WriteException = null;
            await viewModel.DisposeAsync();
            Directory.Delete(folder, true);
        }
    }

    private static void AssertDiagnostic(
        string path,
        string expectedStage,
        string expectedCode)
    {
        var found = false;
        foreach (var line in File.ReadAllLines(path))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.GetProperty("stage").GetString() != expectedStage
                || document.RootElement.GetProperty("errorCode").GetString() != expectedCode)
            {
                continue;
            }
            Assert.Equal(4, document.RootElement.EnumerateObject().Count());
            Assert.False(string.IsNullOrWhiteSpace(
                document.RootElement.GetProperty("appVersion").GetString()));
            found = true;
            break;
        }
        Assert.True(found, $"Expected diagnostic {expectedStage}/{expectedCode} was not written.");
    }

    private static IReadOnlyList<JsonElement> ReadConnectionTransitions(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var items = new List<JsonElement>();
        foreach (var line in File.ReadAllLines(path))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.TryGetProperty("transition", out _))
            {
                items.Add(document.RootElement.Clone());
            }
        }
        return items;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.True(condition(), "Condition was not reached before timeout.");
    }

    private static string TemporaryFolder()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "SamsungSwitchWatch-FailureReporting",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestFactory(TestAgentClient client) : IAgentClientFactory
    {
        public IAgentClient Create(ViewerSettings settings) => client;
    }

    private sealed class QueueFactory(params IAgentClient[] clients) : IAgentClientFactory
    {
        private readonly Queue<IAgentClient> _clients = new(clients);

        public IAgentClient Create(ViewerSettings settings) => _clients.Dequeue();
    }

    private sealed class TestAgentClient(
        bool supportsStatelessV4,
        TaskCompletionSource? startEntered = null,
        Task? startGate = null) : IAgentClient
    {
        private long _firstChangeRequestCursor = -1;

        public bool SupportsStatelessV4 { get; } = supportsStatelessV4;
        public Exception? StartException { get; set; }
        public Queue<EventChangePageDto> ChangePages { get; } = new();
        public long FirstChangeRequestCursor => Interlocked.Read(ref _firstChangeRequestCursor);
        public bool DisposeCalled { get; private set; }
        public event EventHandler<AgentEventChangeDto>? EventChanged { add { } remove { } }
        public event EventHandler<AgentConnectionState>? ConnectionStateChanged;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            startEntered?.TrySetResult();
            if (startGate is not null)
            {
                await startGate.WaitAsync(cancellationToken);
            }
            if (StartException is not null)
            {
                ConnectionStateChanged?.Invoke(this, AgentConnectionState.Offline);
                throw StartException;
            }
            ConnectionStateChanged?.Invoke(
                this,
                SupportsStatelessV4
                    ? AgentConnectionState.Demo
                    : AgentConnectionState.Connected);
        }

        public void EmitState(AgentConnectionState state) =>
            ConnectionStateChanged?.Invoke(this, state);

        public Task<AgentIdentityDto> GetIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgentIdentityDto(
                4,
                "fake",
                "fake-instance",
                new string('A', 64),
                "https",
                8,
                65_536));

        public Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgentSnapshotDto(
                DateTimeOffset.UtcNow,
                AgentConnectionState.Connected,
                [],
                7,
                "test",
                "test",
                "fake-agent"));

        public Task<IReadOnlyList<SwitchEventDto>> GetRecentEventsAsync(
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SwitchEventDto>>([]);

        public Task<EventChangePageDto> GetEventChangesAsync(
            long cursor,
            int limit,
            CancellationToken cancellationToken)
        {
            Interlocked.CompareExchange(ref _firstChangeRequestCursor, cursor, -1);
            return Task.FromResult(
                ChangePages.TryDequeue(out var configuredPage)
                    ? configuredPage
                    : new EventChangePageDto(cursor, cursor, false, []));
        }

        public Task<TelnetExecutionResultDto> TestTelnetAsync(
            TelnetTargetDto target,
            CancellationToken cancellationToken) =>
            Task.FromResult(TelnetResult(target.RequestId));

        public Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken) =>
            Task.FromResult(TelnetResult(request.RequestId));

        public Task<CommandResultDto> ExecuteRegisteredCheckAsync(
            string deviceId,
            string commandId,
            CancellationToken cancellationToken) =>
            Task.FromResult(new CommandResultDto(false, "not used"));

        public Task<ReadOnlyQueryResultDto> ExecuteReadOnlyQueryAsync(
            string deviceId,
            string command,
            CancellationToken cancellationToken) =>
            Task.FromException<ReadOnlyQueryResultDto>(new NotSupportedException());

        public Task<bool> AcknowledgeAsync(
            string eventId,
            CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public ValueTask DisposeAsync()
        {
            DisposeCalled = true;
            return ValueTask.CompletedTask;
        }

        private static TelnetExecutionResultDto TelnetResult(string requestId)
        {
            var now = DateTimeOffset.UtcNow;
            return new TelnetExecutionResultDto(
                4,
                requestId,
                true,
                "privileged",
                "#",
                now,
                now,
                1,
                []);
        }
    }

    private sealed class TestProtector : IViewerSecretProtector
    {
        public string Protect(string plainText) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(plainText));

        public string Unprotect(string protectedText) =>
            Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
    }

    private sealed class TestSettingsPersistence : IViewerSettingsPersistence
    {
        public string? Content { get; private set; }
        public Exception? WriteException { get; set; }

        public string? ReadIfExists(string path) => Content;

        public void WriteAtomically(string path, string content)
        {
            if (WriteException is not null) throw WriteException;
            Content = content;
        }

        public void Quarantine(string path, string destination) => Content = null;
    }

    private sealed class TestMonitoringPersistence : IViewerMonitoringPersistence
    {
        private Exception? _writeException;
        private int _writeCount;

        public string? Content { get; private set; }
        public Exception? WriteException
        {
            get => Volatile.Read(ref _writeException);
            set => Volatile.Write(ref _writeException, value);
        }
        public int WriteCount => Volatile.Read(ref _writeCount);

        public string? ReadIfExists(string path) => Content;

        public void WriteAtomically(string path, string content)
        {
            Interlocked.Increment(ref _writeCount);
            var exception = Volatile.Read(ref _writeException);
            if (exception is not null) throw exception;
            Content = content;
        }

        public void Quarantine(string path, string destination) => Content = null;
    }

    private sealed class SwitchableSynchronizationContext : SynchronizationContext
    {
        private int _dropPosts;

        public bool DropPosts
        {
            get => Volatile.Read(ref _dropPosts) != 0;
            set => Volatile.Write(ref _dropPosts, value ? 1 : 0);
        }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (!DropPosts) callback(state);
        }
    }
}
