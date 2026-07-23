using System.IO;
using System.Text;
using System.Text.Json;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerFailureReportingTests
{
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
            monitoringPersistence.WriteException = new IOException("private path");
            uiContext.DropPosts = true;

            await viewModel.RunMonitoringCycleSafelyAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3));

            AssertDiagnostic(
                diagnosticLog.CurrentPath,
                "monitoring-cycle",
                "VIEWER_MONITOR_STATE_WRITE_FAILED");

            uiContext.DropPosts = false;
            await viewModel.RunMonitoringCycleSafelyAsync(CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3));

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
            Assert.False(original.DisposeCalled);
            Assert.True(replacement.DisposeCalled);
            AssertDiagnostic(
                diagnosticLog.CurrentPath,
                "settings-save-connection",
                "VIEWER_SETTINGS_WRITE_FAILED");
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
            Assert.Equal(3, document.RootElement.EnumerateObject().Count());
            found = true;
            break;
        }
        Assert.True(found, $"Expected diagnostic {expectedStage}/{expectedCode} was not written.");
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

    private sealed class TestAgentClient(bool supportsStatelessV4) : IAgentClient
    {
        public bool SupportsStatelessV4 { get; } = supportsStatelessV4;
        public bool DisposeCalled { get; private set; }
        public event EventHandler<AgentEventChangeDto>? EventChanged { add { } remove { } }
        public event EventHandler<AgentConnectionState>? ConnectionStateChanged;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            ConnectionStateChanged?.Invoke(
                this,
                SupportsStatelessV4
                    ? AgentConnectionState.Demo
                    : AgentConnectionState.Connected);
            return Task.CompletedTask;
        }

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
            CancellationToken cancellationToken) =>
            Task.FromResult(new EventChangePageDto(cursor, cursor, false, []));

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
        public string? Content { get; private set; }
        public Exception? WriteException { get; set; }
        public int WriteCount { get; private set; }

        public string? ReadIfExists(string path) => Content;

        public void WriteAtomically(string path, string content)
        {
            WriteCount++;
            if (WriteException is not null) throw WriteException;
            Content = content;
        }

        public void Quarantine(string path, string destination) => Content = null;
    }

    private sealed class SwitchableSynchronizationContext : SynchronizationContext
    {
        public bool DropPosts { get; set; }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (!DropPosts) callback(state);
        }
    }
}
