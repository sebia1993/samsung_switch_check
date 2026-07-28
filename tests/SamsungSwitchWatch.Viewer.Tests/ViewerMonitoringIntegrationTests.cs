using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerMonitoringIntegrationTests
{
    [Theory]
    [InlineData(
        "Corrupt",
        "VIEWER_MONITOR_STATE_CORRUPT")]
    [InlineData(
        "VersionUnsupported",
        "VIEWER_MONITOR_STATE_VERSION_UNSUPPORTED")]
    [InlineData(
        "StorageUnavailable",
        "VIEWER_MONITOR_STATE_UNAVAILABLE")]
    public async Task NonOperationalMonitoringState_FailsClosedButKeepsManualQueryAvailable(
        string expectedStatusName,
        string expectedErrorCode)
    {
        var expectedStatus = Enum.Parse<ViewerMonitoringLoadStatus>(expectedStatusName);
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var persistence = MonitoringLoadFailurePersistence.For(expectedStatus);
            var monitoringStore = new ViewerMonitoringStore(
                Path.Combine(folder, "monitor.json"),
                persistence);
            var client = new RecordingClient();
            var viewModel = CreateViewModel(folder, devices, client, monitoringStore);
            try
            {
                Assert.Equal(expectedStatus, monitoringStore.LastLoadStatus);
                Assert.False(monitoringStore.IsOperational);
                Assert.Equal(expectedErrorCode, monitoringStore.LoadErrorCode);

                await viewModel.InitializeAsync();
                await viewModel.RunMonitoringCycleAsync();
                await Task.Delay(100);

                var device = Assert.Single(viewModel.Devices);
                Assert.Equal(DeviceHealth.Warning, device.Health);
                Assert.Equal(expectedErrorCode, device.CollectionErrorCode);
                Assert.Equal(0, viewModel.MonitoredCount);
                Assert.Equal(1, viewModel.UnmonitoredCount);
                Assert.Equal(
                    "Agent 연결됨 · 자동 감시 중지됨",
                    viewModel.MiniCurrentStatusText);
                Assert.Equal(
                    "자동 감시 저장소 확인 필요",
                    viewModel.MiniIssueTitle);
                Assert.Contains(expectedErrorCode, viewModel.OperationMessage, StringComparison.Ordinal);
                Assert.Contains(
                    ViewerConnectionMessages.ForCode(expectedErrorCode),
                    viewModel.OperationMessage,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    client.ExecuteRequests,
                    request => request.Purpose.Equals("monitor", StringComparison.Ordinal));

                viewModel.SelectedDevice = device;
                viewModel.ReadOnlyQueryCommand = "show version";

                Assert.True(viewModel.ReadOnlyQueriesEnabled);
                Assert.True(viewModel.ExecuteReadOnlyQueryCommand.CanExecute(null));

                viewModel.ExecuteReadOnlyQueryCommand.Execute(null);
                await WaitUntilAsync(() =>
                    !viewModel.IsReadOnlyQueryRunning
                    && client.ExecuteRequests.Count(request =>
                        request.Purpose.Equals("manual", StringComparison.Ordinal)) == 1);

                Assert.Equal("manual-output", viewModel.ReadOnlyQueryOutput);
                Assert.DoesNotContain(
                    client.ExecuteRequests,
                    request => request.Purpose.Equals("monitor", StringComparison.Ordinal));
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
    public async Task BeginSessionWriteFailure_StillInitializesAgentAndKeepsManualQueryAvailable()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var persistence = new FailAfterWritesMonitoringPersistence(
                successfulWritesBeforeFailure: 0);
            var monitoringStore = new ViewerMonitoringStore(
                Path.Combine(folder, "monitor.json"),
                persistence);
            var client = new RecordingClient();
            var viewModel = CreateViewModel(folder, devices, client, monitoringStore);
            try
            {
                await viewModel.InitializeAsync();

                Assert.Equal(
                    ViewerMonitoringLoadStatus.StorageUnavailable,
                    monitoringStore.LastLoadStatus);
                Assert.False(monitoringStore.IsOperational);
                Assert.Equal(
                    "VIEWER_MONITOR_STATE_UNAVAILABLE",
                    monitoringStore.LoadErrorCode);
                Assert.Equal(AgentConnectionState.Demo, viewModel.ConnectionState);

                var device = Assert.Single(viewModel.Devices);
                Assert.Equal(DeviceHealth.Warning, device.Health);
                Assert.Equal("StoreUnavailable", device.CollectionState);
                Assert.Equal(
                    "VIEWER_MONITOR_STATE_UNAVAILABLE",
                    device.CollectionErrorCode);
                Assert.Equal(0, viewModel.MonitoredCount);
                Assert.Equal(1, viewModel.UnmonitoredCount);
                Assert.Equal(
                    "Agent 연결됨 · 자동 감시 중지됨",
                    viewModel.MiniCurrentStatusText);
                Assert.Equal(
                    "자동 감시 저장소 확인 필요",
                    viewModel.MiniIssueTitle);
                Assert.Contains(
                    "VIEWER_MONITOR_STATE_UNAVAILABLE",
                    viewModel.MiniIssueDetail,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "VIEWER_MONITOR_STATE_UNAVAILABLE",
                    viewModel.OperationMessage,
                    StringComparison.Ordinal);
                Assert.DoesNotContain(
                    client.ExecuteRequests,
                    request => request.Purpose.Equals(
                        "monitor",
                        StringComparison.Ordinal));

                Assert.Contains(
                    viewModel.OperationalStatuses,
                    status =>
                        status.Code == "VIEWER_MONITOR_STATE_UNAVAILABLE"
                        && status.Health == DeviceHealth.Warning);
                Assert.Contains(
                    viewModel.CollectorHealth,
                    metric =>
                        metric.Label == "Viewer 감시"
                        && metric.Value.Contains(
                            "VIEWER_MONITOR_STATE_UNAVAILABLE",
                            StringComparison.Ordinal)
                        && metric.Health == DeviceHealth.Warning);

                viewModel.SelectedDevice = device;
                viewModel.ReadOnlyQueryCommand = "show version";
                Assert.True(viewModel.ExecuteReadOnlyQueryCommand.CanExecute(null));

                viewModel.ExecuteReadOnlyQueryCommand.Execute(null);
                await WaitUntilAsync(() =>
                    !viewModel.IsReadOnlyQueryRunning
                    && client.ExecuteRequests.Count(request =>
                        request.Purpose.Equals(
                            "manual",
                            StringComparison.Ordinal)) == 1);

                Assert.Equal("manual-output", viewModel.ReadOnlyQueryOutput);
                Assert.DoesNotContain(
                    client.ExecuteRequests,
                    request => request.Purpose.Equals(
                        "monitor",
                        StringComparison.Ordinal));
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
    public async Task RuntimeWriteFailure_StopsLaterMonitoringAndRefreshesUnavailablePresentation()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var persistence = new FailAfterWritesMonitoringPersistence(
                successfulWritesBeforeFailure: 1);
            var monitoringStore = new ViewerMonitoringStore(
                Path.Combine(folder, "monitor.json"),
                persistence);
            var client = new RecordingClient();
            var viewModel = CreateViewModel(folder, devices, client, monitoringStore);
            try
            {
                await viewModel.InitializeAsync();
                await WaitUntilAsync(() =>
                    monitoringStore.LastLoadStatus
                        == ViewerMonitoringLoadStatus.StorageUnavailable
                    && viewModel.Devices.SingleOrDefault()?.CollectionState
                        == "StoreUnavailable");

                var monitorRequestsAfterFailure = client.ExecuteRequests.Count(
                    request => request.Purpose.Equals(
                        "monitor",
                        StringComparison.Ordinal));
                Assert.True(monitorRequestsAfterFailure > 0);

                await viewModel.RunMonitoringCycleAsync();
                await viewModel.RunMonitoringCycleSafelyAsync(
                    CancellationToken.None);
                await Task.Delay(100);

                Assert.Equal(
                    monitorRequestsAfterFailure,
                    client.ExecuteRequests.Count(request =>
                        request.Purpose.Equals(
                            "monitor",
                            StringComparison.Ordinal)));

                var device = Assert.Single(viewModel.Devices);
                Assert.Equal(DeviceHealth.Warning, device.Health);
                Assert.Equal("StoreUnavailable", device.CollectionState);
                Assert.Equal(
                    "VIEWER_MONITOR_STATE_UNAVAILABLE",
                    device.CollectionErrorCode);
                Assert.Equal(0, viewModel.MonitoredCount);
                Assert.Equal(1, viewModel.UnmonitoredCount);
                Assert.Equal(
                    "Agent 연결됨 · 자동 감시 중지됨",
                    viewModel.MiniCurrentStatusText);
                Assert.Equal(
                    "자동 감시 저장소 확인 필요",
                    viewModel.MiniIssueTitle);
                Assert.Contains(
                    "VIEWER_MONITOR_STATE_UNAVAILABLE",
                    viewModel.MiniIssueDetail,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "VIEWER_MONITOR_STATE_UNAVAILABLE",
                    viewModel.OperationMessage,
                    StringComparison.Ordinal);
                Assert.Contains(
                    viewModel.OperationalStatuses,
                    status =>
                        status.Code == "VIEWER_MONITOR_STATE_UNAVAILABLE"
                        && status.Health == DeviceHealth.Warning);
                Assert.Contains(
                    viewModel.CollectorHealth,
                    metric =>
                        metric.Label == "Viewer 감시"
                        && metric.Value.Contains(
                            "VIEWER_MONITOR_STATE_UNAVAILABLE",
                            StringComparison.Ordinal)
                        && metric.Health == DeviceHealth.Warning);

                viewModel.SelectedDevice = device;
                viewModel.ReadOnlyQueryCommand = "show version";
                Assert.True(viewModel.ExecuteReadOnlyQueryCommand.CanExecute(null));

                viewModel.ExecuteReadOnlyQueryCommand.Execute(null);
                await WaitUntilAsync(() =>
                    !viewModel.IsReadOnlyQueryRunning
                    && client.ExecuteRequests.Count(request =>
                        request.Purpose.Equals(
                            "manual",
                            StringComparison.Ordinal)) == 1);

                Assert.Equal("manual-output", viewModel.ReadOnlyQueryOutput);
                Assert.Equal(
                    monitorRequestsAfterFailure,
                    client.ExecuteRequests.Count(request =>
                        request.Purpose.Equals(
                            "monitor",
                            StringComparison.Ordinal)));
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
                        && item.State == "Degraded"
                        && item.ErrorCode == "COMMAND_UNSUPPORTED")
                    && viewModel.Devices.Single().Capabilities.Any(item =>
                        item.CommandId == "log_ram"
                        && item.SelectedCli == "show syslog tail num 100"));
                await viewModel.RunMonitoringCycleAsync();
                await WaitUntilAsync(() =>
                    viewModel.Devices.Single().Capabilities.Any(item =>
                        item.CommandId == "interface_status"
                        && item.State == "Unsupported"));

                var device = Assert.Single(viewModel.Devices);
                Assert.Equal(DeviceHealth.Warning, device.Health);
                var port = Assert.Single(device.Capabilities, item => item.CommandId == "interface_status");
                var log = Assert.Single(device.Capabilities, item => item.CommandId == "log_ram");
                Assert.False(port.Supported);
                Assert.True(log.Supported);
                Assert.Equal("show syslog tail num 100", log.SelectedCli);

                var requestCountBeforeReuse = client.ExecuteRequests.Count;
                await viewModel.RunMonitoringCycleAsync();

                var requests = client.ExecuteRequests.ToArray();
                Assert.Equal(requestCountBeforeReuse + 1, requests.Length);
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
    public async Task AgentSwitch_DuringCapabilityFallback_NeverUsesReplacementForOldRequest()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var oldClient = new AgentSwitchRaceClient();
            var replacementClient = new RecordingClient();
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
                new QueueClientFactory(oldClient, replacementClient),
                deviceStore: devices,
                monitoringStore: new ViewerMonitoringStore(Path.Combine(folder, "monitor.json")));
            try
            {
                await viewModel.InitializeAsync();
                await oldClient.OutputEnumerationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                await viewModel.SwitchClientAsync(new ViewerSettings { DemoMode = true })
                    .WaitAsync(TimeSpan.FromSeconds(5));

                oldClient.ReleaseOutputEnumeration.TrySetResult();
                await viewModel.RunMonitoringCycleAsync()
                    .WaitAsync(TimeSpan.FromSeconds(5));

                var replacementRequests = replacementClient.ExecuteRequests.ToArray();
                Assert.NotEmpty(replacementRequests);
                Assert.All(replacementRequests, request => Assert.Equal(2, request.Commands.Count));
            }
            finally
            {
                oldClient.ReleaseOutputEnumeration.TrySetResult();
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
    public async Task PaddedHostConnectionTest_DoesNotBypassMonitoringOperationGate()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var existing = Assert.Single(devices.Load());
            var client = new BlockingMonitoringClient();
            var viewModel = CreateViewModel(folder, devices, client);
            var draft = new ManagedDeviceDraft
            {
                Id = existing.Id,
                DisplayName = existing.DisplayName,
                Model = existing.Model,
                Host = $"  {existing.Host}  ",
                Username = "operator",
                Password = "password",
                MonitoringEnabled = true
            };
            try
            {
                await viewModel.InitializeAsync();
                await client.FirstMonitorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                var connectionTest = viewModel.TestManagedDeviceAsync(draft);

                Assert.Equal(1, client.ExecuteCount);
                Assert.Equal(1, client.MaxConcurrent);

                client.ReleaseMonitor.TrySetResult();
                await connectionTest.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(2, client.ExecuteCount);
                Assert.Equal(1, client.MaxConcurrent);
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
    public async Task DeleteAndRetestSameHost_DoesNotBypassInFlightOperationGate()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var existing = Assert.Single(devices.Load());
            var client = new BlockingConnectionTestClient();
            var viewModel = CreateViewModel(folder, devices, client);
            var draft = new ManagedDeviceDraft
            {
                DisplayName = existing.DisplayName,
                Model = existing.Model,
                Host = existing.Host,
                Username = "operator",
                Password = "password",
                MonitoringEnabled = true
            };
            try
            {
                var first = viewModel.TestManagedDeviceAsync(draft);
                await client.FirstTestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.True(viewModel.DeleteManagedDevice(existing.Id));
                var second = viewModel.TestManagedDeviceAsync(draft);

                Assert.Equal(1, client.TestCount);
                Assert.Equal(1, client.MaxConcurrent);

                client.ReleaseFirst.TrySetResult();
                await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Equal(2, client.TestCount);
                Assert.Equal(1, client.MaxConcurrent);
            }
            finally
            {
                client.ReleaseFirst.TrySetResult();
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task MonitoringDisabledDuringInFlightCycle_DiscardsLateSuccess()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var existing = Assert.Single(devices.Load());
            var client = new BlockingMonitoringClient();
            var viewModel = CreateViewModel(folder, devices, client);
            try
            {
                await viewModel.InitializeAsync();
                await client.FirstMonitorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                viewModel.SetManagedDeviceMonitoring(existing.Id, false);
                var settled = viewModel.RunMonitoringCycleAsync();
                client.ReleaseMonitor.TrySetResult();
                await settled.WaitAsync(TimeSpan.FromSeconds(5));

                var persisted = new ViewerMonitoringStore(
                    Path.Combine(folder, "monitor.json"));
                Assert.Empty(persisted.LoadCapabilities(existing.Id));
                Assert.Null(persisted.GetActiveFailureCode(existing.Id));
                Assert.DoesNotContain(
                    persisted.LoadEvents(),
                    item => item.DeviceId == existing.Id);
                Assert.Equal(DeviceHealth.Empty, Assert.Single(viewModel.Devices).Health);
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
    public async Task DeviceDeletedDuringInFlightCycle_DiscardsLateFailure()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var existing = Assert.Single(devices.Load());
            var client = new BlockingFailureMonitoringClient();
            var viewModel = CreateViewModel(folder, devices, client);
            try
            {
                await viewModel.InitializeAsync();
                await client.FirstMonitorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.True(viewModel.DeleteManagedDevice(existing.Id));
                var settled = viewModel.RunMonitoringCycleAsync();
                client.ReleaseMonitor.TrySetResult();
                await settled.WaitAsync(TimeSpan.FromSeconds(5));

                var persisted = new ViewerMonitoringStore(
                    Path.Combine(folder, "monitor.json"));
                Assert.Null(persisted.GetActiveFailureCode(existing.Id));
                Assert.DoesNotContain(
                    persisted.LoadEvents(),
                    item => item.DeviceId == existing.Id);
                Assert.Empty(viewModel.Devices);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentChannelFailureAfterLifecycleChange_UpdatesGlobalStateOnly(
        bool deleteDevice)
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var existing = Assert.Single(devices.Load());
            var client = new BlockingAgentChannelFailureClient();
            var viewModel = CreateViewModel(folder, devices, client);
            try
            {
                await viewModel.InitializeAsync();
                await client.FirstMonitorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                if (deleteDevice)
                {
                    Assert.True(viewModel.DeleteManagedDevice(existing.Id));
                }
                else
                {
                    viewModel.SetManagedDeviceMonitoring(existing.Id, false);
                }

                client.ReleaseMonitor.TrySetResult();
                await WaitUntilAsync(() =>
                    viewModel.HttpConnectionState == AgentConnectionState.Offline);

                var persisted = new ViewerMonitoringStore(
                    Path.Combine(folder, "monitor.json"));
                Assert.Null(persisted.GetActiveFailureCode(existing.Id));
                Assert.DoesNotContain(
                    persisted.LoadEvents(),
                    item => item.DeviceId == existing.Id
                            && item.Severity == DeviceHealth.Disconnected
                            && !item.Recovered);
                if (deleteDevice)
                {
                    Assert.Empty(viewModel.Devices);
                }
                else
                {
                    Assert.Equal(DeviceHealth.Empty, Assert.Single(viewModel.Devices).Health);
                }
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
    public async Task ChangedConnectionDefinition_StartsWithFreshMonitoringBaseline()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var client = new PortTransitionClient();
            var viewModel = CreateViewModel(folder, devices, client);
            try
            {
                await viewModel.InitializeAsync();
                await WaitUntilAsync(() =>
                    client.ExecuteCount >= 1
                    && viewModel.Devices.Single().Health == DeviceHealth.Normal);

                var existing = Assert.Single(devices.Load());
                var changed = devices.CreateEditDraft(existing.Id);
                changed.Host = "192.0.2.99";
                changed.Password = "replacement-password";
                changed.MonitoringEnabled = true;
                changed.ConnectionVerified = true;
                changed.LastConnectionTestUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                changed.LastConnectionTestCode = "OK";
                client.LinkDown = true;

                var saved = viewModel.SaveManagedDevice(changed);
                Assert.True(saved.MonitoringEnabled);
                await viewModel.RunMonitoringCycleAsync();

                var persisted = new ViewerMonitoringStore(
                    Path.Combine(folder, "monitor.json"));
                Assert.Equal(0, persisted.GetActiveInterfaceConditionCount(existing.Id));
                Assert.DoesNotContain(
                    persisted.LoadEvents(),
                    item => item.DeviceId == existing.Id
                            && item.Kind == "포트 상태");
                Assert.Equal(DeviceHealth.Normal, Assert.Single(viewModel.Devices).Health);
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
    public async Task ReenabledPersistedDevice_DiscardsLegacyBaselineBeforeFirstResult()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var existing = Assert.Single(devices.Load());
            var monitoringPath = Path.Combine(folder, "monitor.json");
            var legacyState = new ViewerMonitoringStore(monitoringPath);
            Assert.Empty(legacyState.RecordOutput(
                existing,
                "show port status",
                "Port Admin Link Speed Duplex\r\n1 Enabled Up 1000M Full"));
            devices.SetMonitoring(existing.Id, false);
            Assert.Contains(
                "SHOW PORT STATUS",
                File.ReadAllText(monitoringPath),
                StringComparison.Ordinal);

            var client = new PortTransitionClient { LinkDown = true };
            var viewModel = CreateViewModel(folder, devices, client);
            try
            {
                await viewModel.InitializeAsync();
                Assert.Equal(0, client.ExecuteCount);

                viewModel.SetManagedDeviceMonitoring(existing.Id, true);
                await viewModel.RunMonitoringCycleAsync();

                var persisted = new ViewerMonitoringStore(monitoringPath);
                Assert.Equal(0, persisted.GetActiveInterfaceConditionCount(existing.Id));
                Assert.DoesNotContain(
                    persisted.LoadEvents(),
                    item => item.DeviceId == existing.Id
                            && item.Kind == "포트 상태");
                Assert.Equal(DeviceHealth.Normal, Assert.Single(viewModel.Devices).Health);
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
    public async Task CredentialChangedDuringInFlightAuthenticationFailure_DiscardsOldFailureAndUsesNewPassword()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var existing = Assert.Single(devices.Load());
            var client = new BlockingAuthenticationFailureThenSuccessClient();
            var viewModel = CreateViewModel(folder, devices, client);
            try
            {
                await viewModel.InitializeAsync();
                await client.FirstMonitorStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                var changed = devices.CreateEditDraft(existing.Id);
                changed.Password = "replacement-password";
                changed.MonitoringEnabled = true;
                changed.ConnectionVerified = true;
                changed.LastConnectionTestUtc = DateTimeOffset.UtcNow.AddMinutes(5);
                changed.LastConnectionTestCode = "OK";
                var saved = viewModel.SaveManagedDevice(changed);

                var settled = viewModel.RunMonitoringCycleAsync();
                client.ReleaseFirstMonitor.TrySetResult();
                await settled.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.False(viewModel.IsMonitoringCredentialBlocked(existing.Id));
                var current = Assert.Single(devices.Load());
                Assert.True(current.ConnectionVerified);
                Assert.True(current.MonitoringEnabled);
                Assert.Equal("OK", current.LastConnectionTestCode);
                Assert.Equal(
                    "replacement-password",
                    devices.GetSecrets(existing.Id).Password);

                var requests = client.ExecuteRequests.ToArray();
                Assert.Equal(2, requests.Length);
                Assert.Equal("password", requests[0].Password);
                Assert.Equal("replacement-password", requests[1].Password);

                var persisted = new ViewerMonitoringStore(
                    Path.Combine(folder, "monitor.json"));
                Assert.Null(persisted.GetActiveFailureCode(existing.Id));
                Assert.DoesNotContain(
                    persisted.LoadEvents(),
                    item => item.DeviceId == existing.Id);
                Assert.DoesNotContain(
                    "인증 실패",
                    viewModel.OperationMessage,
                    StringComparison.Ordinal);
                Assert.Equal(DeviceHealth.Normal, Assert.Single(viewModel.Devices).Health);
            }
            finally
            {
                client.ReleaseFirstMonitor.TrySetResult();
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task DisplayOnlySave_ReconcilesPersistedMonitoringEventsIntoDashboard()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var existing = Assert.Single(devices.Load());
            var monitoringStore = new ViewerMonitoringStore(
                Path.Combine(folder, "monitor.json"));
            var viewModel = CreateViewModel(
                folder,
                devices,
                new PortTransitionClient(),
                monitoringStore);
            try
            {
                await viewModel.InitializeAsync();
                await WaitUntilAsync(() =>
                    Assert.Single(viewModel.Devices).Health == DeviceHealth.Normal);

                var persisted = Assert.Single(
                    monitoringStore.RecordFailure(existing, "TCP_TIMEOUT"));
                Assert.DoesNotContain(
                    viewModel.RecentEvents,
                    item => item.AgentEventId == persisted.AgentEventId);

                var renamed = devices.CreateEditDraft(existing.Id);
                renamed.DisplayName = "ACCESS-SW-RENAMED";
                var saved = viewModel.SaveManagedDevice(renamed);

                Assert.Equal("ACCESS-SW-RENAMED", saved.DisplayName);
                await WaitUntilAsync(() =>
                    viewModel.RecentEvents.Any(item =>
                        item.AgentEventId == persisted.AgentEventId));
                var displayed = Assert.Single(
                    viewModel.RecentEvents,
                    item => item.AgentEventId == persisted.AgentEventId);
                Assert.False(displayed.Recovered);
                Assert.Equal(DeviceHealth.Disconnected, Assert.Single(viewModel.Devices).Health);
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

    [Theory]
    [InlineData(
        "Corrupt",
        "VIEWER_DEVICE_STORE_CORRUPT")]
    [InlineData(
        "VersionUnsupported",
        "VIEWER_DEVICE_STORE_VERSION_UNSUPPORTED")]
    [InlineData(
        "StorageUnavailable",
        "VIEWER_DEVICE_STORE_UNAVAILABLE")]
    [InlineData(
        "MissingAfterObserved",
        "VIEWER_DEVICE_STORE_UNAVAILABLE")]
    public async Task RuntimeDeviceStoreFailure_StopsMonitoringWithoutFalseHealthyState(
        string failureName,
        string expectedErrorCode)
    {
        var folder = TemporaryFolder();
        try
        {
            var persistence = new RuntimeManagedDevicePersistence();
            var devices = new ManagedDeviceStore(
                "viewer-devices.json",
                new TestProtector(),
                persistence);
            devices.Save(new ManagedDeviceDraft
            {
                DisplayName = "ACCESS-SW-01",
                Model = "IES4224GP",
                Host = "192.0.2.11",
                Username = "operator",
                Password = "password",
                MonitoringEnabled = true,
                ConnectionVerified = true,
                LastConnectionTestUtc = DateTimeOffset.UtcNow,
                LastConnectionTestCode = "OK"
            });
            var client = new RecordingClient();
            var monitoringPath = Path.Combine(folder, "monitor.json");
            var viewModel = CreateViewModel(
                folder,
                devices,
                client,
                new ViewerMonitoringStore(monitoringPath));
            try
            {
                await viewModel.InitializeAsync();
                await WaitUntilAsync(() =>
                    client.ExecuteRequests.Count > 0
                    && viewModel.NormalCount == 1);
                await viewModel.RunMonitoringCycleAsync();

                var requestsBeforeFailure = client.ExecuteRequests.Count;
                var monitoringStateBeforeFailure =
                    File.ReadAllText(monitoringPath, Encoding.UTF8);
                persistence.Fail(failureName);

                await viewModel.RunMonitoringCycleAsync();
                await viewModel.RunMonitoringCycleSafelyAsync(
                    CancellationToken.None);

                Assert.False(devices.IsOperational);
                Assert.Equal(expectedErrorCode, devices.LoadErrorCode);
                Assert.Equal(requestsBeforeFailure, client.ExecuteRequests.Count);
                Assert.Equal(
                    monitoringStateBeforeFailure,
                    File.ReadAllText(monitoringPath, Encoding.UTF8));

                var device = Assert.Single(viewModel.Devices);
                Assert.Equal(DeviceHealth.Warning, device.Health);
                Assert.Equal("DeviceStoreUnavailable", device.CollectionState);
                Assert.Equal(expectedErrorCode, device.CollectionErrorCode);
                Assert.Equal(0, viewModel.NormalCount);
                Assert.Equal(1, viewModel.WarningCount);
                Assert.Equal(0, viewModel.MonitoredCount);
                Assert.Equal(1, viewModel.UnmonitoredCount);
                Assert.Equal(DeviceHealth.Warning, viewModel.MiniIssueHealth);
                Assert.Contains("장비 목록 저장소", viewModel.MiniIssueTitle, StringComparison.Ordinal);
                Assert.Contains("자동 감시 중지", viewModel.OperationMessage, StringComparison.Ordinal);
                Assert.Contains(expectedErrorCode, viewModel.OperationMessage, StringComparison.Ordinal);
                Assert.Contains(viewModel.CollectorHealth, item =>
                    item.Label == "Viewer 감시"
                    && item.Value.Contains(expectedErrorCode, StringComparison.Ordinal)
                    && item.Health == DeviceHealth.Warning);
                Assert.Contains(viewModel.OperationalStatuses, item =>
                    item.Code == expectedErrorCode
                    && item.Detail.Contains("자동 감시 중지", StringComparison.Ordinal)
                    && item.Health == DeviceHealth.Warning);
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

    [Theory]
    [InlineData("Corrupt")]
    [InlineData("VersionUnsupported")]
    [InlineData("StorageUnavailable")]
    public async Task StartupDeviceStoreFailure_DoesNotStartMonitoringSession(
        string failureName)
    {
        var folder = TemporaryFolder();
        try
        {
            var persistence = new RuntimeManagedDevicePersistence();
            var devices = new ManagedDeviceStore(
                "viewer-devices.json",
                new TestProtector(),
                persistence);
            devices.Save(new ManagedDeviceDraft
            {
                DisplayName = "ACCESS-SW-01",
                Model = "IES4224GP",
                Host = "192.0.2.11",
                Username = "operator",
                Password = "password",
                MonitoringEnabled = true,
                ConnectionVerified = true,
                LastConnectionTestUtc = DateTimeOffset.UtcNow,
                LastConnectionTestCode = "OK"
            });
            persistence.Fail(failureName);
            var client = new RecordingClient();
            var monitoringPath = Path.Combine(folder, "monitor.json");
            var viewModel = CreateViewModel(
                folder,
                devices,
                client,
                new ViewerMonitoringStore(monitoringPath));

            await viewModel.InitializeAsync();

            Assert.Empty(client.ExecuteRequests);
            Assert.False(File.Exists(monitoringPath));

            await viewModel.DisposeAsync();

            Assert.False(File.Exists(monitoringPath));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task RuntimeDeviceStoreFailure_DisposeDoesNotCloseSession_AndRestartReportsGap()
    {
        var folder = TemporaryFolder();
        try
        {
            var persistence = new RuntimeManagedDevicePersistence();
            var devices = new ManagedDeviceStore(
                "viewer-devices.json",
                new TestProtector(),
                persistence);
            var profile = devices.Save(new ManagedDeviceDraft
            {
                DisplayName = "ACCESS-SW-01",
                Model = "IES4224GP",
                Host = "192.0.2.11",
                Username = "operator",
                Password = "password",
                MonitoringEnabled = true,
                ConnectionVerified = true,
                LastConnectionTestUtc = DateTimeOffset.UtcNow,
                LastConnectionTestCode = "OK"
            });
            var monitoringPath = Path.Combine(folder, "monitor.json");
            var viewModel = CreateViewModel(
                folder,
                devices,
                new RecordingClient(),
                new ViewerMonitoringStore(monitoringPath));

            await viewModel.InitializeAsync();
            await WaitUntilAsync(() =>
                Assert.Single(viewModel.Devices).Health == DeviceHealth.Normal);

            persistence.Fail("StorageUnavailable");
            await viewModel.RunMonitoringCycleAsync();
            await WaitUntilAsync(() =>
                Assert.Single(viewModel.Devices).CollectionState
                    == "DeviceStoreUnavailable");

            var stateAtFailure = ReadSessionTimestamps(monitoringPath);
            Assert.NotNull(stateAtFailure.LastHeartbeatUtc);
            Assert.Null(stateAtFailure.LastStoppedUtc);

            await viewModel.DisposeAsync();

            var stateAfterDispose = ReadSessionTimestamps(monitoringPath);
            Assert.Equal(
                stateAtFailure.LastHeartbeatUtc,
                stateAfterDispose.LastHeartbeatUtc);
            Assert.Equal(
                stateAtFailure.LastStoppedUtc,
                stateAfterDispose.LastStoppedUtc);

            BackdateOpenMonitoringSession(
                monitoringPath,
                DateTimeOffset.UtcNow.AddMinutes(-1));
            var restartedStore = new ViewerMonitoringStore(monitoringPath);

            var gap = Assert.Single(restartedStore.BeginSession([profile]));

            Assert.Equal(profile.Id, gap.DeviceId);
            Assert.Equal(DeviceHealth.Warning, gap.Severity);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task DelayedAgentStartFailure_DoesNotHeartbeatAfterDeviceStoreWriteFailure()
    {
        var folder = TemporaryFolder();
        try
        {
            var persistence = new RuntimeManagedDevicePersistence();
            var devices = new ManagedDeviceStore(
                "viewer-devices.json",
                new TestProtector(),
                persistence);
            var profile = devices.Save(new ManagedDeviceDraft
            {
                DisplayName = "ACCESS-SW-01",
                Model = "IES4224GP",
                Host = "192.0.2.11",
                Username = "operator",
                Password = "password",
                MonitoringEnabled = true,
                ConnectionVerified = true,
                LastConnectionTestUtc = DateTimeOffset.UtcNow,
                LastConnectionTestCode = "OK"
            });
            var pendingSave = devices.CreateEditDraft(profile.Id);
            pendingSave.DisplayName = "ACCESS-SW-RENAMED";
            var monitoringPath = Path.Combine(folder, "monitor.json");
            var client = new DelayedMonitorStartFailureClient();
            var viewModel = CreateViewModel(
                folder,
                devices,
                client,
                new ViewerMonitoringStore(monitoringPath));
            try
            {
                await viewModel.InitializeAsync();
                await client.MonitorStartEntered.Task.WaitAsync(
                    TimeSpan.FromSeconds(5));
                var stateBeforeFailure =
                    File.ReadAllText(monitoringPath, Encoding.UTF8);

                persistence.FailWrites();
                Assert.Throws<IOException>(() => devices.Save(pendingSave));
                Assert.False(devices.IsOperational);

                client.ReleaseMonitorStart.TrySetResult();
                await client.MonitorStartCompleted.Task.WaitAsync(
                    TimeSpan.FromSeconds(5));
                await WaitUntilAsync(() =>
                    Assert.Single(viewModel.Devices).CollectionState
                        == "DeviceStoreUnavailable");

                Assert.Equal(
                    stateBeforeFailure,
                    File.ReadAllText(monitoringPath, Encoding.UTF8));

                await viewModel.DisposeAsync();

                Assert.Equal(
                    stateBeforeFailure,
                    File.ReadAllText(monitoringPath, Encoding.UTF8));
            }
            finally
            {
                client.ReleaseMonitorStart.TrySetResult();
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public async Task SuccessfulAgentResult_DoesNotHeartbeatAfterConcurrentDeviceStoreWriteFailure()
    {
        var folder = TemporaryFolder();
        try
        {
            var persistence = new RuntimeManagedDevicePersistence();
            var devices = new ManagedDeviceStore(
                "viewer-devices.json",
                new TestProtector(),
                persistence);
            var profile = devices.Save(new ManagedDeviceDraft
            {
                DisplayName = "ACCESS-SW-01",
                Model = "IES4224GP",
                Host = "192.0.2.11",
                Username = "operator",
                Password = "password",
                MonitoringEnabled = true,
                ConnectionVerified = true,
                LastConnectionTestUtc = DateTimeOffset.UtcNow,
                LastConnectionTestCode = "OK"
            });
            var pendingSave = devices.CreateEditDraft(profile.Id);
            pendingSave.DisplayName = "ACCESS-SW-RENAMED";
            var monitoringPath = Path.Combine(folder, "monitor.json");
            var client = new BlockingMonitoringClient();
            var viewModel = CreateViewModel(
                folder,
                devices,
                client,
                new ViewerMonitoringStore(monitoringPath));
            try
            {
                await viewModel.InitializeAsync();
                await client.FirstMonitorStarted.Task.WaitAsync(
                    TimeSpan.FromSeconds(5));
                var stateBeforeFailure =
                    File.ReadAllText(monitoringPath, Encoding.UTF8);

                persistence.FailWrites();
                Assert.Throws<IOException>(() => devices.Save(pendingSave));
                Assert.False(devices.IsOperational);

                client.ReleaseMonitor.TrySetResult();
                await WaitUntilAsync(() =>
                    Assert.Single(viewModel.Devices).CollectionState
                        == "DeviceStoreUnavailable");

                Assert.Equal(
                    stateBeforeFailure,
                    File.ReadAllText(monitoringPath, Encoding.UTF8));

                await viewModel.DisposeAsync();

                Assert.Equal(
                    stateBeforeFailure,
                    File.ReadAllText(monitoringPath, Encoding.UTF8));
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
                await WaitUntilAsync(() =>
                    client.StartCount >= 2 &&
                    viewModel.HttpConnectionState == AgentConnectionState.Offline);

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
    public async Task PortLinkTransition_UpdatesHealthWithoutTreatingUnassignedPortAsCritical()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = CreateVerifiedDevices(folder, 1);
            var client = new PortTransitionClient();
            var viewModel = CreateViewModel(folder, devices, client);
            try
            {
                await viewModel.InitializeAsync();
                await WaitUntilAsync(() =>
                    client.ExecuteCount >= 1
                    && viewModel.Devices.Single().Health == DeviceHealth.Normal);

                client.LinkDown = true;
                await viewModel.RunMonitoringCycleAsync();

                var warning = Assert.Single(viewModel.Devices);
                Assert.Equal(DeviceHealth.Warning, warning.Health);
                Assert.Contains("포트 상태 변경", warning.Summary, StringComparison.Ordinal);
                var active = Assert.Single(viewModel.RecentEvents, item =>
                    item.Kind == "포트 상태" && !item.Recovered);
                Assert.Contains("영향 대상은 지정되지 않았습니다", active.Detail, StringComparison.Ordinal);

                await viewModel.RunMonitoringCycleAsync();
                Assert.Single(viewModel.RecentEvents, item =>
                    item.Kind == "포트 상태" && !item.Recovered);

                client.LinkDown = false;
                await viewModel.RunMonitoringCycleAsync();

                Assert.Equal(DeviceHealth.Normal, Assert.Single(viewModel.Devices).Health);
                Assert.Contains(viewModel.RecentEvents, item =>
                    item.Kind == "복구"
                    && item.Title.Contains("Port 1", StringComparison.Ordinal));
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
        StatelessClientBase client,
        ViewerMonitoringStore? monitoringStore = null) =>
        new(
            new ViewerSettings { DemoMode = true },
            new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
            new ClientFactory(client),
            deviceStore: devices,
            monitoringStore: monitoringStore
                             ?? new ViewerMonitoringStore(Path.Combine(folder, "monitor.json")));

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

    private static (
        DateTimeOffset? LastHeartbeatUtc,
        DateTimeOffset? LastStoppedUtc) ReadSessionTimestamps(string path)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(path, Encoding.UTF8));
        var root = document.RootElement;
        return (
            ReadNullableTimestamp(root, "LastHeartbeatUtc"),
            ReadNullableTimestamp(root, "LastStoppedUtc"));
    }

    private static DateTimeOffset? ReadNullableTimestamp(
        JsonElement root,
        string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return property.GetDateTimeOffset();
    }

    private static void BackdateOpenMonitoringSession(
        string path,
        DateTimeOffset timestamp)
    {
        var root = JsonNode.Parse(
                File.ReadAllText(path, Encoding.UTF8))
            ?.AsObject()
            ?? throw new InvalidDataException("MONITOR_STATE_NULL");
        root["LastStartedUtc"] = timestamp;
        root["LastHeartbeatUtc"] = timestamp;
        root["LastStoppedUtc"] = null;
        File.WriteAllText(
            path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
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

    private sealed class RuntimeManagedDevicePersistence : IManagedDevicePersistence
    {
        public string? Content { get; private set; }
        public Exception? ReadException { get; private set; }
        public Exception? WriteException { get; private set; }

        public string? ReadIfExists(string path)
        {
            if (ReadException is not null) throw ReadException;
            return Content;
        }

        public void WriteAtomically(string path, string content)
        {
            if (WriteException is not null) throw WriteException;
            Content = content;
        }

        public void Quarantine(string path, string destination) =>
            Content = null;

        public void Fail(string failureName)
        {
            switch (failureName)
            {
                case "Corrupt":
                    Content = "{";
                    break;
                case "VersionUnsupported":
                    Content = """{"SchemaVersion":2,"Devices":[]}""";
                    break;
                case "StorageUnavailable":
                    ReadException = new IOException("simulated device-store read failure");
                    break;
                case "MissingAfterObserved":
                    Content = null;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(failureName),
                        failureName,
                        null);
            }
        }

        public void FailWrites() =>
            WriteException =
                new IOException("simulated device-store write failure");
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

    private sealed class QueueClientFactory(params IAgentClient[] clients) : IAgentClientFactory
    {
        private readonly Queue<IAgentClient> _clients = new(clients);

        public IAgentClient Create(ViewerSettings settings) => _clients.Dequeue();
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
                    ? Syslog((1, "link state stable"))
                    : PortStatus(("1", "Up")),
                false,
                DateTimeOffset.UtcNow)).ToArray();

        protected static string PortStatus(params (string PortId, string Link)[] ports)
        {
            var lines = new List<string> { "Port Admin Link Speed Duplex" };
            lines.AddRange(ports.Select(port =>
                $"{port.PortId} Enabled {port.Link} 1000M Full"));
            return string.Join("\r\n", lines);
        }

        protected static string Syslog(params (int Sequence, string Message)[] entries) =>
            string.Join(
                "\r\n",
                entries.Select(entry =>
                    $"[{entry.Sequence}] 00:00:{entry.Sequence:00} 2026-07-23\r\n"
                    + $"\"{entry.Message}\"\r\n"
                    + "level: 6, module: 6, function: 1, and event no.: 1"));
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
                || command.Equals("show interfaces status", StringComparison.OrdinalIgnoreCase)
                    ? "% Invalid input detected"
                    : command.Equals("show sylog tail num 100", StringComparison.OrdinalIgnoreCase)
                        ? "% Invalid command"
                        : Syslog((1, "invalid password event")),
                false,
                DateTimeOffset.UtcNow)).ToArray();
            return Task.FromResult(Result(request.RequestId, outputs));
        }
    }

    private sealed class AgentSwitchRaceClient : StatelessClientBase
    {
        private int _executeCount;

        public TaskCompletionSource OutputEnumerationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOutputEnumeration { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _executeCount) != 1)
            {
                return Task.FromResult(Result(request.RequestId, NormalOutputs(request)));
            }

            var outputs = request.Commands.Select(command => new TelnetCommandOutputDto(
                command,
                command.Equals("show port status", StringComparison.OrdinalIgnoreCase)
                    ? "% Invalid command"
                    : Syslog((1, "link state stable")),
                false,
                DateTimeOffset.UtcNow)).ToArray();
            return Task.FromResult(Result(
                request.RequestId,
                new BlockingCommandOutputList(
                    outputs,
                    OutputEnumerationStarted,
                    ReleaseOutputEnumeration)));
        }
    }

    private sealed class RecordingClient : StatelessClientBase
    {
        public ConcurrentQueue<TelnetExecuteRequestDto> ExecuteRequests { get; } = new();

        public override Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken)
        {
            ExecuteRequests.Enqueue(request);
            var outputs = request.Purpose.Equals("manual", StringComparison.Ordinal)
                ? (IReadOnlyList<TelnetCommandOutputDto>)
                [
                    new(request.Commands[0], "manual-output", false, DateTimeOffset.UtcNow)
                ]
                : NormalOutputs(request);
            return Task.FromResult(Result(request.RequestId, outputs));
        }
    }

    private sealed class MonitoringLoadFailurePersistence : IViewerMonitoringPersistence
    {
        private readonly string? _content;
        private readonly Exception? _readException;

        private MonitoringLoadFailurePersistence(string? content, Exception? readException)
        {
            _content = content;
            _readException = readException;
        }

        public static MonitoringLoadFailurePersistence For(ViewerMonitoringLoadStatus status) =>
            status switch
            {
                ViewerMonitoringLoadStatus.Corrupt =>
                    new("{not-json", null),
                ViewerMonitoringLoadStatus.VersionUnsupported =>
                    new("""{"SchemaVersion": 999}""", null),
                ViewerMonitoringLoadStatus.StorageUnavailable =>
                    new(null, new UnauthorizedAccessException("simulated read denial")),
                _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
            };

        public string? ReadIfExists(string path)
        {
            if (_readException is not null) throw _readException;
            return _content;
        }

        public void WriteAtomically(string path, string content) =>
            throw new InvalidOperationException("A non-operational store must not write.");

        public void Quarantine(string path, string destination)
        {
        }
    }

    private sealed class FailAfterWritesMonitoringPersistence(
        int successfulWritesBeforeFailure) : IViewerMonitoringPersistence
    {
        private readonly object _sync = new();
        private string? _content;
        private int _successfulWrites;

        public string? ReadIfExists(string path)
        {
            lock (_sync)
            {
                return _content;
            }
        }

        public void WriteAtomically(string path, string content)
        {
            lock (_sync)
            {
                if (_successfulWrites >= successfulWritesBeforeFailure)
                {
                    throw new IOException("simulated monitoring state write failure");
                }

                _content = content;
                _successfulWrites++;
            }
        }

        public void Quarantine(string path, string destination)
        {
            lock (_sync)
            {
                _content = null;
            }
        }
    }

    private sealed class BlockingCommandOutputList(
        IReadOnlyList<TelnetCommandOutputDto> items,
        TaskCompletionSource enumerationStarted,
        TaskCompletionSource releaseEnumeration) : IReadOnlyList<TelnetCommandOutputDto>
    {
        public int Count => items.Count;
        public TelnetCommandOutputDto this[int index] => items[index];

        public IEnumerator<TelnetCommandOutputDto> GetEnumerator()
        {
            enumerationStarted.TrySetResult();
            releaseEnumeration.Task.GetAwaiter().GetResult();
            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class BlockingConnectionTestClient : StatelessClientBase
    {
        private int _active;
        private int _testCount;
        private int _maxConcurrent;

        public TaskCompletionSource FirstTestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int TestCount => Volatile.Read(ref _testCount);
        public int MaxConcurrent => Volatile.Read(ref _maxConcurrent);

        public override async Task<TelnetExecutionResultDto> TestTelnetAsync(
            TelnetTargetDto target,
            CancellationToken cancellationToken)
        {
            var testCount = Interlocked.Increment(ref _testCount);
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(ref _maxConcurrent, active);
            try
            {
                if (testCount == 1)
                {
                    FirstTestStarted.TrySetResult();
                    await ReleaseFirst.Task.WaitAsync(cancellationToken);
                }
                return Result(target.RequestId, []);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        public override Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result(request.RequestId, NormalOutputs(request)));
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

        public override Task<TelnetExecutionResultDto> TestTelnetAsync(
            TelnetTargetDto target,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executeCount);
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(ref _maxConcurrent, active);
            try
            {
                return Task.FromResult(Result(target.RequestId, []));
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

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

    private sealed class BlockingFailureMonitoringClient : StatelessClientBase
    {
        public TaskCompletionSource FirstMonitorStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseMonitor { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken)
        {
            FirstMonitorStarted.TrySetResult();
            await ReleaseMonitor.Task.WaitAsync(cancellationToken);
            throw new AgentClientException("TCP_TIMEOUT", AgentConnectionState.Stale);
        }
    }

    private sealed class BlockingAgentChannelFailureClient : StatelessClientBase
    {
        public TaskCompletionSource FirstMonitorStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseMonitor { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken)
        {
            FirstMonitorStarted.TrySetResult();
            await ReleaseMonitor.Task.WaitAsync(cancellationToken);
            throw new AgentClientException(
                "AGENT_UNREACHABLE",
                AgentConnectionState.Offline);
        }
    }

    private sealed class BlockingRecoveryMonitoringClient : StatelessClientBase
    {
        private int _executeCount;

        public TaskCompletionSource FirstMonitorStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseMonitor { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ExecuteCount => Volatile.Read(ref _executeCount);

        public override async Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _executeCount) == 1)
            {
                FirstMonitorStarted.TrySetResult();
                await ReleaseMonitor.Task.WaitAsync(cancellationToken);
            }

            return Result(request.RequestId, NormalOutputs(request));
        }
    }

    private sealed class BlockingAuthenticationFailureThenSuccessClient : StatelessClientBase
    {
        private int _executeCount;

        public TaskCompletionSource FirstMonitorStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstMonitor { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<TelnetExecuteRequestDto> ExecuteRequests { get; } = new();

        public override async Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken)
        {
            ExecuteRequests.Enqueue(request);
            if (Interlocked.Increment(ref _executeCount) == 1)
            {
                FirstMonitorStarted.TrySetResult();
                await ReleaseFirstMonitor.Task.WaitAsync(cancellationToken);
                throw new AgentClientException("AUTH_FAILED", AgentConnectionState.Stale);
            }

            return Result(request.RequestId, NormalOutputs(request));
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

    private sealed class DelayedMonitorStartFailureClient : StatelessClientBase
    {
        private int _startCount;

        public TaskCompletionSource MonitorStartEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseMonitorStart { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource MonitorStartCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task StartAsync(
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _startCount) == 1)
            {
                RaiseConnectionState(AgentConnectionState.Demo);
                return;
            }

            MonitorStartEntered.TrySetResult();
            try
            {
                await ReleaseMonitorStart.Task.WaitAsync(cancellationToken);
                throw new AgentClientException(
                    "AGENT_UNREACHABLE",
                    AgentConnectionState.Offline);
            }
            finally
            {
                MonitorStartCompleted.TrySetResult();
            }
        }

        public override Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "A failed monitor preflight must not execute a device command.");
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

    private sealed class PortTransitionClient : StatelessClientBase
    {
        private int _executeCount;

        public int ExecuteCount => Volatile.Read(ref _executeCount);

        public bool LinkDown { get; set; }

        public override Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _executeCount);
            var outputs = request.Commands.Select(command => new TelnetCommandOutputDto(
                command,
                command.Contains("log", StringComparison.OrdinalIgnoreCase)
                || command.Contains("sylog", StringComparison.OrdinalIgnoreCase)
                    ? Syslog((1, "link state observed"))
                    : PortStatus(("1", LinkDown ? "Down" : "Up")),
                false,
                DateTimeOffset.UtcNow)).ToArray();
            return Task.FromResult(Result(request.RequestId, outputs));
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
