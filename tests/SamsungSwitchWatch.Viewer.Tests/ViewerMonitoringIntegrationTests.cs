using System.Collections.Concurrent;
using System.IO;
using System.Text;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerMonitoringIntegrationTests
{
    [Fact]
    public void CorruptProtectedCredentials_AreDisabledAndCanBeReentered()
    {
        var folder = TemporaryFolder();
        try
        {
            var path = Path.Combine(folder, "devices.json");
            File.WriteAllText(path, """
                {
                  "SchemaVersion": 1,
                  "Devices": [{
                    "Id": "corrupt-device",
                    "DisplayName": "ACCESS-SW-CORRUPT",
                    "Model": "IES4224GP",
                    "Host": "192.0.2.10",
                    "Port": 23,
                    "ProtectedUsername": "corrupt",
                    "ProtectedPassword": "corrupt",
                    "ProtectedEnablePassword": "corrupt",
                    "MonitoringEnabled": true,
                    "ConnectionVerified": true
                  }]
                }
                """, new UTF8Encoding(false));
            var store = new ManagedDeviceStore(path, new SelectiveProtector());

            var profile = Assert.Single(store.Load());

            Assert.False(profile.ConnectionVerified);
            Assert.False(profile.MonitoringEnabled);
            Assert.Equal("VIEWER_CREDENTIAL_CORRUPT", profile.LastConnectionTestCode);
            Assert.Equal(string.Empty, store.CreateEditDraft(profile.Id).Username);
            var failure = Assert.Throws<InvalidDataException>(() => store.GetSecrets(profile.Id));
            Assert.Equal("VIEWER_CREDENTIAL_CORRUPT", failure.Message);

            var missingPassword = store.CreateEditDraft(profile.Id);
            missingPassword.Username = "operator-new";
            Assert.Throws<InvalidDataException>(() => store.Save(missingPassword));

            var repaired = store.CreateEditDraft(profile.Id);
            repaired.Username = "operator-new";
            repaired.Password = "password-new";
            var saved = store.Save(repaired);

            Assert.False(saved.MonitoringEnabled);
            Assert.False(saved.ConnectionVerified);
            Assert.Equal("VIEWER_CONNECTION_TEST_REQUIRED", saved.LastConnectionTestCode);
            Assert.Equal(
                new ManagedDeviceSecrets("operator-new", "password-new", null),
                store.GetSecrets(profile.Id));
            Assert.Empty(Directory.GetFiles(folder, "*.corrupt-*"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void DuplicateDeviceAddress_IsRejected()
    {
        var folder = TemporaryFolder();
        try
        {
            var store = CreateVerifiedDevices(folder, 1);
            var duplicate = new ManagedDeviceDraft
            {
                DisplayName = "ACCESS-SW-DUPLICATE",
                Model = "IES4226XP",
                Host = "192.0.2.11",
                Username = "operator-two",
                Password = "password-two"
            };

            var failure = Assert.Throws<InvalidDataException>(() => store.Save(duplicate));

            Assert.Contains("이미 등록", failure.Message, StringComparison.Ordinal);
            Assert.Single(store.Load());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task UnsupportedCommands_AreWarningsAndKnownFallbackIsReused()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var client = new CapabilityClient();
            var viewModel = CreateViewModel(folder, devices, client);
            try
            {
                await viewModel.InitializeAsync();
                await WaitUntilAsync(() =>
                    client.ExecuteRequests.Count >= 2
                    && viewModel.Devices.Single().Capabilities.Any(item =>
                        item.CommandId == "interface_status"
                        && item.State == "Unsupported")
                    && viewModel.Devices.Single().Capabilities.Any(item =>
                        item.CommandId == "log_ram"
                        && item.SelectedCli == "show syslog tail num 100"));

                var device = Assert.Single(viewModel.Devices);
                Assert.Equal(DeviceHealth.Warning, device.Health);
                var port = Assert.Single(device.Capabilities, item => item.CommandId == "interface_status");
                var log = Assert.Single(device.Capabilities, item => item.CommandId == "log_ram");
                Assert.False(port.Supported);
                Assert.True(log.Supported);
                Assert.Equal("show syslog tail num 100", log.SelectedCli);

                await viewModel.RunMonitoringCycleAsync();

                var requests = client.ExecuteRequests.ToArray();
                Assert.Equal(3, requests.Length);
                Assert.Equal(["show syslog tail num 100"], requests[^1].Commands);
                var stateJson = File.ReadAllText(Path.Combine(folder, "monitor.json"));
                Assert.DoesNotContain("SHOW PORT STATUS", stateJson, StringComparison.Ordinal);
                Assert.DoesNotContain("invalid password event", stateJson, StringComparison.Ordinal);
            }
            finally
            {
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task ManualAndMonitoringCommands_ForSameTargetAreSerialized()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var client = new BlockingMonitoringClient();
            var viewModel = CreateViewModel(folder, devices, client);
            try
            {
                await viewModel.InitializeAsync();
                await client.FirstMonitorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                viewModel.SelectedDevice = Assert.Single(viewModel.Devices);
                viewModel.ReadOnlyQueryCommand = "show running-config";
                viewModel.ExecuteReadOnlyQueryCommand.Execute(null);
                await WaitUntilAsync(() => viewModel.IsReadOnlyQueryRunning);
                await Task.Delay(100);

                Assert.Equal(1, client.ExecuteCount);
                Assert.Equal(1, client.MaxConcurrent);

                client.ReleaseMonitor.TrySetResult();
                await WaitUntilAsync(() =>
                    !viewModel.IsReadOnlyQueryRunning
                    && client.ExecuteCount == 2);

                Assert.Equal(1, client.MaxConcurrent);
                Assert.Equal("manual-output", viewModel.ReadOnlyQueryOutput);
                Assert.NotEqual(DeviceHealth.Disconnected, Assert.Single(viewModel.Devices).Health);
            }
            finally
            {
                client.ReleaseMonitor.TrySetResult();
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task MonitoringCycle_UsesAtMostTwoConcurrentDevices()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 3);
            var client = new BoundedConcurrencyClient();
            var viewModel = CreateViewModel(folder, devices, client);
            try
            {
                await viewModel.InitializeAsync();
                await client.TwoConcurrent.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(2, client.ExecuteCount);
                Assert.Equal(2, client.MaxConcurrent);

                client.ReleaseAll.TrySetResult();
                await WaitUntilAsync(() => client.ExecuteCount == 3 && client.Active == 0);

                Assert.Equal(2, client.MaxConcurrent);
            }
            finally
            {
                client.ReleaseAll.TrySetResult();
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task AgentPreflightFailure_StopsCycleWithoutPerDeviceRequests()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 3);
            var client = new OfflineAfterInitializationClient();
            var viewModel = CreateViewModel(folder, devices, client);
            try
            {
                await viewModel.InitializeAsync();
                await WaitUntilAsync(() => client.StartCount >= 2);

                Assert.Equal(0, client.ExecuteCount);
                Assert.Equal(AgentConnectionState.Offline, viewModel.HttpConnectionState);
                Assert.All(viewModel.Devices, item =>
                    Assert.NotEqual(DeviceHealth.Disconnected, item.Health));
            }
            finally
            {
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task DeviceFailureAndRecovery_UpdateDashboardHealth()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var client = new FailOnceMonitoringClient();
            var viewModel = CreateViewModel(folder, devices, client);
            try
            {
                await viewModel.InitializeAsync();
                await WaitUntilAsync(() =>
                    viewModel.Devices.Single().Health == DeviceHealth.Disconnected);

                Assert.Contains("TCP_TIMEOUT", viewModel.Devices.Single().Summary, StringComparison.Ordinal);

                await viewModel.RunMonitoringCycleAsync();

                var recovered = Assert.Single(viewModel.Devices);
                Assert.Equal(DeviceHealth.Normal, recovered.Health);
                Assert.DoesNotContain("실패", recovered.Summary, StringComparison.Ordinal);
                Assert.Contains(viewModel.RecentEvents, item => item.Recovered);
            }
            finally
            {
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task TurningMonitoringOff_ClearsCurrentDisconnectedState()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var client = new FailOnceMonitoringClient();
            var viewModel = CreateViewModel(folder, devices, client);
            try
            {
                await viewModel.InitializeAsync();
                await WaitUntilAsync(() =>
                    viewModel.Devices.Single().Health == DeviceHealth.Disconnected);
                var id = viewModel.Devices.Single().Id;

                viewModel.SetManagedDeviceMonitoring(id, false);

                Assert.NotEqual(DeviceHealth.Disconnected, Assert.Single(viewModel.Devices).Health);
                Assert.Null(new ViewerMonitoringStore(
                    Path.Combine(folder, "monitor.json")).GetActiveFailureCode(id));
            }
            finally
            {
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task AgentTransportFailure_PreservesLastDeviceHealthAndUpdatesAgentStateOnly()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var client = new AgentDropAfterSuccessClient();
            var viewModel = CreateViewModel(folder, devices, client);
            try
            {
                await viewModel.InitializeAsync();
                await WaitUntilAsync(() =>
                    client.ExecuteCount >= 1
                    && viewModel.Devices.Single().Health == DeviceHealth.Normal);
                var deviceId = Assert.Single(viewModel.Devices).Id;

                client.DropAgentTransport = true;
                await viewModel.RunMonitoringCycleAsync();

                Assert.Equal(AgentConnectionState.Offline, viewModel.ConnectionState);
                Assert.Equal(DeviceHealth.Normal, Assert.Single(viewModel.Devices).Health);
                Assert.Null(new ViewerMonitoringStore(
                    Path.Combine(folder, "monitor.json")).GetActiveFailureCode(deviceId));
                Assert.DoesNotContain(viewModel.RecentEvents, item =>
                    item.DeviceId == deviceId
                    && item.Severity == DeviceHealth.Disconnected
                    && !item.Recovered);
            }
            finally
            {
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static ManagedDeviceStore CreateVerifiedDevices(string folder, int count)
    {
        var store = new ManagedDeviceStore(
            Path.Combine(folder, "devices.json"),
            new TestProtector());
        for (var index = 1; index <= count; index++)
        {
            store.Save(new ManagedDeviceDraft
            {
                DisplayName = $"ACCESS-SW-{index:00}",
                Model = "IES4224GP",
                Host = $"192.0.2.{index + 10}",
                Username = "operator",
                Password = "password",
                MonitoringEnabled = true,
                ConnectionVerified = true,
                LastConnectionTestUtc = DateTimeOffset.UtcNow.AddSeconds(index),
                LastConnectionTestCode = "OK"
            });
        }
        return store;
    }

    private static DashboardViewModel CreateViewModel(
        string folder,
        ManagedDeviceStore devices,
        StatelessClientBase client) =>
        new(
            new ViewerSettings { DemoMode = true },
            new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
            new ClientFactory(client),
            deviceStore: devices,
            monitoringStore: new ViewerMonitoringStore(Path.Combine(folder, "monitor.json")));

    private static string TemporaryFolder()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "SamsungSwitchWatch-Monitoring",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class TestProtector : IViewerSecretProtector
    {
        public string Protect(string plainText) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes("protected:" + plainText));

        public string Unprotect(string protectedText)
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
            return decoded["protected:".Length..];
        }
    }

    private sealed class SelectiveProtector : IViewerSecretProtector
    {
        public string Protect(string plainText) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes("protected:" + plainText));

        public string Unprotect(string protectedText)
        {
            if (protectedText == "corrupt") throw new InvalidDataException("simulated DPAPI failure");
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
            return decoded["protected:".Length..];
        }
    }

    private sealed class ClientFactory(StatelessClientBase client) : IAgentClientFactory
    {
        public IAgentClient Create(ViewerSettings settings) => client;
    }

    private abstract class StatelessClientBase : IAgentClient
    {
        public bool SupportsStatelessV4 => true;
        public event EventHandler<AgentEventChangeDto>? EventChanged { add { } remove { } }
        public event EventHandler<AgentConnectionState>? ConnectionStateChanged;

        public virtual Task StartAsync(CancellationToken cancellationToken)
        {
            ConnectionStateChanged?.Invoke(this, AgentConnectionState.Demo);
            return Task.CompletedTask;
        }

        protected void RaiseConnectionState(AgentConnectionState state) =>
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

        public virtual Task<TelnetExecutionResultDto> TestTelnetAsync(
            TelnetTargetDto target,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result(target.RequestId, []));

        public abstract Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken);

        public Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromException<AgentSnapshotDto>(new NotSupportedException());

        public Task<IReadOnlyList<SwitchEventDto>> GetRecentEventsAsync(
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SwitchEventDto>>([]);

        public Task<EventChangePageDto> GetEventChangesAsync(
            long cursor,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult(new EventChangePageDto(cursor, cursor, false, []));

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

        public Task<bool> AcknowledgeAsync(string eventId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        protected static TelnetExecutionResultDto Result(
            string requestId,
            IReadOnlyList<TelnetCommandOutputDto> commands)
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
                commands);
        }

        protected static IReadOnlyList<TelnetCommandOutputDto> NormalOutputs(
            TelnetExecuteRequestDto request) =>
            request.Commands.Select(command => new TelnetCommandOutputDto(
                command,
                command.Contains("log", StringComparison.OrdinalIgnoreCase)
                    || command.Contains("sylog", StringComparison.OrdinalIgnoreCase)
                    ? "log-a"
                    : "Port 1 Up",
                false,
                DateTimeOffset.UtcNow)).ToArray();
    }

    private sealed class CapabilityClient : StatelessClientBase
    {
        public ConcurrentQueue<TelnetExecuteRequestDto> ExecuteRequests { get; } = new();

        public override Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken)
        {
            ExecuteRequests.Enqueue(request);
            var outputs = request.Commands.Select(command => new TelnetCommandOutputDto(
                command,
                command.Equals("show port status", StringComparison.OrdinalIgnoreCase)
                    ? "% Invalid input detected"
                    : command.Equals("show sylog tail num 100", StringComparison.OrdinalIgnoreCase)
                        ? "% Invalid command"
                        : "2026-07-23 invalid password event",
                false,
                DateTimeOffset.UtcNow)).ToArray();
            return Task.FromResult(Result(request.RequestId, outputs));
        }
    }

    private sealed class BlockingMonitoringClient : StatelessClientBase
    {
        private int _active;
        private int _executeCount;
        private int _maxConcurrent;

        public TaskCompletionSource FirstMonitorStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseMonitor { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ExecuteCount => Volatile.Read(ref _executeCount);
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public override async Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executeCount);
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(ref _maxConcurrent, active);
            try
            {
                if (request.Purpose == "monitor" && ExecuteCount == 1)
                {
                    FirstMonitorStarted.TrySetResult();
                    await ReleaseMonitor.Task.WaitAsync(cancellationToken);
                }
                var outputs = request.Purpose == "manual"
                    ? (IReadOnlyList<TelnetCommandOutputDto>)
                    [
                        new(request.Commands[0], "manual-output", false, DateTimeOffset.UtcNow)
                    ]
                    : NormalOutputs(request);
                return Result(request.RequestId, outputs);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class BoundedConcurrencyClient : StatelessClientBase
    {
        private int _active;
        private int _executeCount;
        private int _maxConcurrent;

        public TaskCompletionSource TwoConcurrent { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseAll { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Active => Volatile.Read(ref _active);
        public int ExecuteCount => Volatile.Read(ref _executeCount);
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public override async Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executeCount);
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(ref _maxConcurrent, active);
            if (active == 2) TwoConcurrent.TrySetResult();
            try
            {
                await ReleaseAll.Task.WaitAsync(cancellationToken);
                return Result(request.RequestId, NormalOutputs(request));
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class OfflineAfterInitializationClient : StatelessClientBase
    {
        private int _startCount;
        public int StartCount => Volatile.Read(ref _startCount);
        public int ExecuteCount { get; private set; }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _startCount);
            if (call == 1)
            {
                RaiseConnectionState(AgentConnectionState.Demo);
                return Task.CompletedTask;
            }
            RaiseConnectionState(AgentConnectionState.Offline);
            throw new AgentClientException("AGENT_UNREACHABLE", AgentConnectionState.Offline);
        }

        public override Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return Task.FromResult(Result(request.RequestId, NormalOutputs(request)));
        }
    }

    private sealed class FailOnceMonitoringClient : StatelessClientBase
    {
        private int _executeCount;

        public override Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _executeCount) == 1)
            {
                throw new AgentClientException("TCP_TIMEOUT", AgentConnectionState.Stale);
            }
            return Task.FromResult(Result(request.RequestId, NormalOutputs(request)));
        }
    }

    private sealed class AgentDropAfterSuccessClient : StatelessClientBase
    {
        private int _executeCount;

        public int ExecuteCount => Volatile.Read(ref _executeCount);

        public bool DropAgentTransport { get; set; }

        public override Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executeCount);
            if (DropAgentTransport)
            {
                RaiseConnectionState(AgentConnectionState.Offline);
                throw new AgentClientException(
                    "AGENT_UNREACHABLE",
                    AgentConnectionState.Offline);
            }
            return Task.FromResult(Result(request.RequestId, NormalOutputs(request)));
        }
    }

    private static void UpdateMaximum(ref int maximum, int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref maximum);
            if (candidate <= current) return;
            if (Interlocked.CompareExchange(ref maximum, candidate, current) == current) return;
        }
    }
}
