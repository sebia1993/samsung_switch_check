using System.IO;
using System.Net.Security;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerManagedDeviceTests
{
    [Fact]
    public void DeviceStore_EncryptsSecretsPreservesBlankEditsAndDefaultsMonitoringOff()
    {
        var folder = TemporaryFolder();
        try
        {
            var path = Path.Combine(folder, "devices.json");
            var store = new ManagedDeviceStore(path, new TestProtector());
            var draft = Draft("login-secret", "enable-secret");
            draft.MonitoringEnabled = true;
            draft.ConnectionVerified = false;
            var saved = store.Save(draft);

            Assert.False(saved.MonitoringEnabled);
            Assert.False(saved.ConnectionVerified);
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("login-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("enable-secret", json, StringComparison.Ordinal);
            Assert.DoesNotContain("operator", json, StringComparison.Ordinal);
            Assert.Equal(new ManagedDeviceSecrets("operator", "login-secret", "enable-secret"), store.GetSecrets(saved.Id));

            var updated = store.Save(new ManagedDeviceDraft
            {
                Id = saved.Id,
                DisplayName = "ACCESS-SW-01-R",
                Model = saved.Model,
                Host = saved.Host,
                Username = store.CreateEditDraft(saved.Id).Username
            });
            Assert.Equal("ACCESS-SW-01-R", updated.DisplayName);
            Assert.Equal(new ManagedDeviceSecrets("operator", "login-secret", "enable-secret"), store.GetSecrets(saved.Id));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void DeviceStore_MigratesLegacyPlainUsernameToProtectedValue()
    {
        var folder = TemporaryFolder();
        try
        {
            var path = Path.Combine(folder, "devices.json");
            File.WriteAllText(path, """
            {
              "SchemaVersion":1,
              "Devices":[{
                "Id":"legacy",
                "DisplayName":"ACCESS-SW-LEGACY",
                "Model":"IES4224GP",
                "Host":"192.0.2.20",
                "Port":23,
                "Username":"legacy-operator",
                "ProtectedPassword":"cHJvdGVjdGVkOnB3",
                "MonitoringEnabled":false,
                "ConnectionVerified":false
              }]
            }
            """);
            var store = new ManagedDeviceStore(path, new TestProtector());

            var profile = Assert.Single(store.Load());

            Assert.Equal(ManagedDeviceLoadStatus.Ok, store.LastLoadStatus);
            Assert.Equal("legacy-operator", store.CreateEditDraft(profile.Id).Username);
            var migrated = File.ReadAllText(path);
            Assert.DoesNotContain("legacy-operator", migrated, StringComparison.Ordinal);
            Assert.DoesNotContain("\"Username\"", migrated, StringComparison.Ordinal);
            Assert.Contains("\"ProtectedUsername\"", migrated, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    public static TheoryData<string> InvalidDeviceStoreDocuments => new()
    {
        "null",
        """{"SchemaVersion":2,"Devices":[]}""",
        """{"SchemaVersion":1,"Devices":null}""",
        """
        {
          "SchemaVersion":1,
          "Devices":[
            {
              "Id":"valid",
              "DisplayName":"ACCESS-SW-VALID",
              "Model":"IES4224GP",
              "Host":"192.0.2.20",
              "Port":23,
              "ProtectedUsername":"cHJvdGVjdGVkOm9wZXJhdG9y",
              "ProtectedPassword":"cHJvdGVjdGVkOnB3"
            },
            {
              "Id":"invalid",
              "DisplayName":"ACCESS-SW-INVALID",
              "Model":"IES4224GP",
              "Host":"",
              "Port":23,
              "ProtectedUsername":"cHJvdGVjdGVkOm9wZXJhdG9y",
              "ProtectedPassword":"cHJvdGVjdGVkOnB3"
            }
          ]
        }
        """
    };

    [Theory]
    [MemberData(nameof(InvalidDeviceStoreDocuments))]
    public void DeviceStore_InvalidEnvelopeIsQuarantinedWithoutPartialDeviceLoad(string content)
    {
        var persistence = new TestManagedDevicePersistence { Content = content };
        var store = new ManagedDeviceStore("viewer-devices.json", new TestProtector(), persistence);

        var loaded = store.Load();

        Assert.Empty(loaded);
        Assert.Equal(ManagedDeviceLoadStatus.Corrupt, store.LastLoadStatus);
        Assert.Equal(1, persistence.QuarantineCount);
        Assert.Null(persistence.Content);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void DeviceStore_ReadFailurePreservesOriginalAndDoesNotQuarantine(Type exceptionType)
    {
        const string original = """{"SchemaVersion":1,"Devices":[]}""";
        var persistence = new TestManagedDevicePersistence
        {
            Content = original,
            ReadException = (Exception)Activator.CreateInstance(exceptionType, "simulated storage failure")!
        };
        var store = new ManagedDeviceStore("viewer-devices.json", new TestProtector(), persistence);

        Assert.Empty(store.Load());

        Assert.Equal(ManagedDeviceLoadStatus.StorageUnavailable, store.LastLoadStatus);
        Assert.Equal(0, persistence.QuarantineCount);
        Assert.Equal(original, persistence.Content);
    }

    [Fact]
    public async Task Dashboard_ShowsDeviceStoreCorruptionInsteadOfAnOrdinaryEmptyList()
    {
        var folder = TemporaryFolder();
        try
        {
            var persistence = new TestManagedDevicePersistence { Content = "null" };
            var devices = new ManagedDeviceStore(
                "viewer-devices.json",
                new TestProtector(),
                persistence);
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
                new StatelessFactory(new StatelessFakeClient()),
                deviceStore: devices);
            try
            {
                viewModel.ReloadManagedDevices();

                Assert.Empty(viewModel.Devices);
                Assert.Contains("VIEWER_DEVICE_STORE_CORRUPT", viewModel.OperationMessage, StringComparison.Ordinal);
                Assert.Contains("다시 등록", viewModel.OperationMessage, StringComparison.Ordinal);
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
    public async Task Dashboard_ShowsDeviceStoreIoFailureWithoutCallingItCorrupt()
    {
        var folder = TemporaryFolder();
        try
        {
            var persistence = new TestManagedDevicePersistence
            {
                Content = """{"SchemaVersion":1,"Devices":[]}""",
                ReadException = new IOException("simulated storage failure")
            };
            var devices = new ManagedDeviceStore(
                "viewer-devices.json",
                new TestProtector(),
                persistence);
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
                new StatelessFactory(new StatelessFakeClient()),
                deviceStore: devices);
            try
            {
                viewModel.ReloadManagedDevices();

                Assert.Empty(viewModel.Devices);
                Assert.Contains("VIEWER_DEVICE_STORE_UNAVAILABLE", viewModel.OperationMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("VIEWER_DEVICE_STORE_CORRUPT", viewModel.OperationMessage, StringComparison.Ordinal);
                Assert.Equal(0, persistence.QuarantineCount);
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
    public void DeviceStore_SaveFailureDoesNotReplacePreviouslyPersistedDevices()
    {
        var persistence = new TestManagedDevicePersistence();
        var store = new ManagedDeviceStore("viewer-devices.json", new TestProtector(), persistence);
        var saved = store.Save(Draft("pw", null));
        var previous = persistence.Content;
        var edit = store.CreateEditDraft(saved.Id);
        edit.DisplayName = "ACCESS-SW-CHANGED";
        persistence.WriteException = new IOException("simulated atomic write failure");

        Assert.Throws<IOException>(() => store.Save(edit));

        Assert.Equal(previous, persistence.Content);
        persistence.WriteException = null;
        Assert.Equal("ACCESS-SW-01", Assert.Single(store.Load()).DisplayName);
    }

    [Fact]
    public void FailedConnectionTest_ForcesMonitoringOffEvenWhenConnectionFieldsAreUnchanged()
    {
        var folder = TemporaryFolder();
        try
        {
            var store = new ManagedDeviceStore(Path.Combine(folder, "devices.json"), new TestProtector());
            var draft = Draft("pw", null);
            draft.ConnectionVerified = true;
            draft.MonitoringEnabled = true;
            draft.LastConnectionTestUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
            draft.LastConnectionTestCode = "OK";
            var verified = store.Save(draft);
            Assert.True(verified.MonitoringEnabled);

            var failed = store.Save(new ManagedDeviceDraft
            {
                Id = verified.Id,
                DisplayName = verified.DisplayName,
                Model = verified.Model,
                Host = verified.Host,
                Username = store.CreateEditDraft(verified.Id).Username,
                ConnectionVerified = false,
                MonitoringEnabled = true,
                LastConnectionTestUtc = DateTimeOffset.UtcNow,
                LastConnectionTestCode = "AUTH_FAILED"
            });

            Assert.False(failed.ConnectionVerified);
            Assert.False(failed.MonitoringEnabled);
            Assert.Equal("AUTH_FAILED", failed.LastConnectionTestCode);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("show port status", true)]
    [InlineData("show running-config", true)]
    [InlineData("show", false)]
    [InlineData("show port status\nreload", false)]
    [InlineData("show port | include up", false)]
    [InlineData("show $secret", false)]
    [InlineData("show port > file", false)]
    [InlineData("configure terminal", false)]
    public void ViewerCommandPolicy_MatchesSharedCorePolicy(string command, bool expected) =>
        Assert.Equal(expected, ManagedDeviceValidator.IsSingleShowCommand(command));

    [Fact]
    public void MonitoringStore_DeduplicatesFailuresEmitsRecoveryAndIgnoresSyslogReordering()
    {
        var folder = TemporaryFolder();
        try
        {
            var store = new ViewerMonitoringStore(Path.Combine(folder, "monitor.json"));
            var device = Profile();

            Assert.Single(store.RecordFailure(device, "TCP_TIMEOUT"));
            Assert.Empty(store.RecordFailure(device, "TCP_TIMEOUT"));

            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Up"))));
            Assert.Equal("TCP_TIMEOUT", store.GetActiveFailureCode(device.Id));
            Assert.False(Assert.Single(store.LoadEvents()).Recovered);
            var recovered = store.RecordSuccess(device);
            Assert.Equal(2, recovered.Count);
            Assert.All(recovered, item => Assert.True(item.Recovered));
            Assert.Equal(DeviceHealth.Normal, recovered[^1].Severity);

            Assert.Empty(store.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((1, "line-a"), (2, "line-b"))));
            Assert.Empty(store.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((2, "line-b"), (1, "line-a"))));
            var newLog = store.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((3, "line-c"), (2, "line-b"), (1, "line-a")));
            Assert.Single(newLog);
            Assert.Equal("새 로그", newLog[0].Kind);

            var json = File.ReadAllText(Path.Combine(folder, "monitor.json"));
            Assert.DoesNotContain("line-a", json, StringComparison.Ordinal);
            Assert.DoesNotContain("Port 1 Up", json, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_FailureCodeChangeUpdatesOneActiveIncidentUntilSuccess()
    {
        var folder = TemporaryFolder();
        try
        {
            var store = new ViewerMonitoringStore(Path.Combine(folder, "monitor.json"));
            var device = Profile();

            var initial = Assert.Single(store.RecordFailure(device, "TCP_TIMEOUT"));
            var changed = Assert.Single(store.RecordFailure(device, "TELNET_SESSION_CLOSED"));

            Assert.Equal(initial.AgentEventId, changed.AgentEventId);
            Assert.Equal(initial.Sequence, changed.Sequence);
            Assert.Equal("TELNET_SESSION_CLOSED", changed.Detail);
            Assert.False(changed.Recovered);
            Assert.Equal("TELNET_SESSION_CLOSED", store.GetActiveFailureCode(device.Id));
            var persistedActive = Assert.Single(store.LoadEvents());
            Assert.Equal(initial.AgentEventId, persistedActive.AgentEventId);
            Assert.False(persistedActive.Recovered);

            var recovery = store.RecordSuccess(device);

            Assert.Equal(2, recovery.Count);
            Assert.All(recovery, item => Assert.True(item.Recovered));
            Assert.Null(store.GetActiveFailureCode(device.Id));
            Assert.Equal(2, store.LoadEvents().Count);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void MonitoringStore_GapRebaselinesLegacyStateWithoutReportingStaleChange(int schemaVersion)
    {
        var folder = TemporaryFolder();
        try
        {
            var path = Path.Combine(folder, "monitor.json");
            File.WriteAllText(path, $$"""
            {
              "SchemaVersion": {{schemaVersion}},
              "NextSequence": 0,
              "LastStoppedUtc": "2000-01-01T00:00:00+00:00",
              "Baselines": {
                "sw-01\nSHOW PORT STATUS": {
                  "OutputHash": "legacy-hash",
                  "LineHashes": []
                }
              },
              "Events": []
            }
            """);
            var store = new ViewerMonitoringStore(path);
            var device = Profile();

            var gap = Assert.Single(store.BeginSession([device]));
            Assert.Equal("감시 공백", gap.Kind);
            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Down"))));
            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Up"))));
            var currentChange = Assert.Single(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Down"))));

            Assert.Equal("포트 상태", currentChange.Kind);
            Assert.Contains("\"SchemaVersion\": 3", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_InterfaceLifecycleIsSemanticDeduplicatedAndRecoverable()
    {
        var folder = TemporaryFolder();
        try
        {
            var store = new ViewerMonitoringStore(Path.Combine(folder, "monitor.json"));
            var device = Profile();

            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Up"))));
            var opened = Assert.Single(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Down"))));

            Assert.Equal(DeviceHealth.Warning, opened.Severity);
            Assert.Equal("포트 상태", opened.Kind);
            Assert.Equal("Port 24 Link Down", opened.Title);
            Assert.Contains("영향 대상은 지정되지 않았습니다", opened.Detail, StringComparison.Ordinal);
            Assert.True(opened.IsActiveCondition);
            Assert.Equal(1, store.GetActiveInterfaceConditionCount(device.Id));

            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Down"))));
            Assert.Single(store.LoadEvents());

            var recovered = store.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Up")));

            Assert.Equal(2, recovered.Count);
            Assert.All(recovered, item => Assert.True(item.Recovered));
            Assert.Equal("복구", recovered[^1].Kind);
            Assert.Equal(0, store.GetActiveInterfaceConditionCount(device.Id));
            Assert.Equal(2, store.LoadEvents().Count);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_RetentionPreservesActiveConditionsUntilRecovery()
    {
        var folder = TemporaryFolder();
        try
        {
            var path = Path.Combine(folder, "monitor.json");
            var device = Profile();
            var store = new ViewerMonitoringStore(path);

            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Up"))));
            var interfaceEvent = Assert.Single(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Down"))));
            var failureEvent = Assert.Single(store.RecordFailure(device, "TCP_TIMEOUT"));
            store.EndSession();

            var state = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            state["LastStoppedUtc"] = "2000-01-01T00:00:00+00:00";
            state["LastHeartbeatUtc"] = "2000-01-01T00:00:00+00:00";
            File.WriteAllText(
                path,
                state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var restarted = new ViewerMonitoringStore(path);
            var fillerDevices = Enumerable.Range(1, 600)
                .Select(index => new ManagedDeviceProfile
                {
                    Id = $"filler-{index:D4}",
                    DisplayName = $"FILLER-{index:D4}",
                    Model = "IES4224GP",
                    Host = "192.0.2.20",
                    Port = 23,
                    ProtectedUsername = "protected",
                    ProtectedPassword = "protected",
                    ConnectionVerified = true,
                    MonitoringEnabled = true
                })
                .ToArray();

            var gapEvents = restarted.BeginSession(fillerDevices);
            var retained = restarted.LoadEvents();

            Assert.Equal(600, gapEvents.Count);
            Assert.Equal(500, retained.Count);
            Assert.Contains(retained, item => item.AgentEventId == interfaceEvent.AgentEventId);
            Assert.Contains(retained, item => item.AgentEventId == failureEvent.AgentEventId);
            Assert.DoesNotContain(retained, item => item.AgentEventId == gapEvents[0].AgentEventId);
            Assert.Contains(retained, item => item.AgentEventId == gapEvents[^1].AgentEventId);
            Assert.True(restarted.Acknowledge(interfaceEvent.AgentEventId));
            Assert.True(restarted.Acknowledge(failureEvent.AgentEventId));

            var reloaded = new ViewerMonitoringStore(path);
            Assert.Equal(1, reloaded.GetActiveInterfaceConditionCount(device.Id));
            Assert.Equal("TCP_TIMEOUT", reloaded.GetActiveFailureCode(device.Id));
            Assert.True(reloaded.LoadEvents().Single(item =>
                item.AgentEventId == interfaceEvent.AgentEventId).Acknowledged);
            Assert.True(reloaded.LoadEvents().Single(item =>
                item.AgentEventId == failureEvent.AgentEventId).Acknowledged);

            var changedFailure = Assert.Single(
                reloaded.RecordFailure(device, "TELNET_SESSION_CLOSED"));
            Assert.Equal(failureEvent.AgentEventId, changedFailure.AgentEventId);
            Assert.Equal("TELNET_SESSION_CLOSED", changedFailure.Detail);

            var interfaceRecovery = reloaded.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Up")));
            var failureRecovery = reloaded.RecordSuccess(device);

            Assert.Equal(2, interfaceRecovery.Count);
            Assert.Equal(2, failureRecovery.Count);
            Assert.All(interfaceRecovery, item => Assert.True(item.Recovered));
            Assert.All(failureRecovery, item => Assert.True(item.Recovered));
            Assert.Equal(0, reloaded.GetActiveInterfaceConditionCount(device.Id));
            Assert.Null(reloaded.GetActiveFailureCode(device.Id));

            var finalState = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(500, finalState["Events"]!.AsArray().Count);
            Assert.Empty(finalState["ActiveFailures"]!.AsObject());
            Assert.Empty(finalState["ActiveInterfaceConditions"]!.AsObject());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_RetentionNeverDeletesActiveInterfaceConditionsWhenLimitIsExhausted()
    {
        var folder = TemporaryFolder();
        try
        {
            var path = Path.Combine(folder, "monitor.json");
            var store = new ViewerMonitoringStore(path);
            var device = Profile();
            var upPorts = Enumerable.Range(1, 501)
                .Select(index => (PortId: index.ToString(), Link: "Up"))
                .ToArray();
            var downPorts = Enumerable.Range(1, 500)
                .Select(index => (PortId: index.ToString(), Link: "Down"))
                .ToArray();

            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(upPorts)));
            var interfaceEvents = store.RecordOutput(
                device,
                "show port status",
                PortStatus(downPorts));
            var lastInterfaceEvent = Assert.Single(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("501", "Down"))));

            Assert.Equal(500, interfaceEvents.Count);
            var state = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert.Equal(501, state["Events"]!.AsArray().Count);
            Assert.Equal(501, state["ActiveInterfaceConditions"]!.AsObject().Count);
            Assert.Empty(state["ActiveFailures"]!.AsObject());
            Assert.True(store.Acknowledge(interfaceEvents[0].AgentEventId));
            Assert.True(store.Acknowledge(lastInterfaceEvent.AgentEventId));

            var reloaded = new ViewerMonitoringStore(path);
            Assert.Equal(501, reloaded.GetActiveInterfaceConditionCount(device.Id));
            var recovery = reloaded.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Up")));
            Assert.Equal(2, recovery.Count);
            Assert.All(recovery, item => Assert.True(item.Recovered));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_InitialDownAndPortSetChangesDoNotCreateFalseEvents()
    {
        var folder = TemporaryFolder();
        try
        {
            var device = Profile();
            var initialDownStore = new ViewerMonitoringStore(
                Path.Combine(folder, "initial-down.json"));

            Assert.Empty(initialDownStore.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Down"))));
            Assert.Empty(initialDownStore.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Down"))));
            Assert.Empty(initialDownStore.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Up"))));
            Assert.Equal(0, initialDownStore.GetActiveInterfaceConditionCount(device.Id));

            var changingSetStore = new ViewerMonitoringStore(
                Path.Combine(folder, "changing-set.json"));
            Assert.Empty(changingSetStore.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Up"), ("2", "Up"))));
            Assert.Empty(changingSetStore.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Up"), ("3", "Down"))));
            Assert.Empty(changingSetStore.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Up"), ("2", "Up"), ("3", "Down"))));

            Assert.Empty(changingSetStore.LoadEvents());
            Assert.Equal(0, changingSetStore.GetActiveInterfaceConditionCount(device.Id));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_ParserFailureDoesNotAdvanceInterfaceOrLogBaselines()
    {
        var folder = TemporaryFolder();
        try
        {
            var store = new ViewerMonitoringStore(Path.Combine(folder, "monitor.json"));
            var device = Profile();
            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Up"))));
            Assert.Empty(store.RecordOutput(
                device,
                "show syslog tail num 100",
                Syslog((1, "line-a"))));

            var rejectedInterface = store.TryRecordParsedOutput(
                device,
                "show port status",
                "unrecognized interface response");
            var rejectedLog = store.TryRecordParsedOutput(
                device,
                "show syslog tail num 100",
                "unrecognized log response");

            Assert.False(rejectedInterface.Accepted);
            Assert.False(rejectedLog.Accepted);
            Assert.NotNull(rejectedInterface.ErrorCode);
            Assert.NotNull(rejectedLog.ErrorCode);
            Assert.Empty(store.LoadEvents());

            Assert.Single(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Down"))));
            var log = Assert.Single(store.RecordOutput(
                device,
                "show syslog tail num 100",
                Syslog((1, "line-a"), (2, "line-b"))));
            Assert.Equal("새 시스템 로그 1건", log.Title);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_GapPreservesActiveInterfaceConditionAndRecoversFromFreshBaseline()
    {
        var folder = TemporaryFolder();
        try
        {
            var path = Path.Combine(folder, "monitor.json");
            var device = Profile();
            var store = new ViewerMonitoringStore(path);
            Assert.Empty(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Up"))));
            Assert.Single(store.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Down"))));
            store.EndSession();

            var state = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            state["LastStoppedUtc"] = "2000-01-01T00:00:00+00:00";
            state["LastHeartbeatUtc"] = "2000-01-01T00:00:00+00:00";
            File.WriteAllText(path, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var restarted = new ViewerMonitoringStore(path);
            Assert.Single(restarted.BeginSession([device]));
            Assert.Equal(1, restarted.GetActiveInterfaceConditionCount(device.Id));
            Assert.Empty(restarted.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Down"))));
            Assert.Equal(1, restarted.GetActiveInterfaceConditionCount(device.Id));

            var recovered = restarted.RecordOutput(
                device,
                "show port status",
                PortStatus(("24", "Up")));

            Assert.Equal(2, recovered.Count);
            Assert.Equal(0, restarted.GetActiveInterfaceConditionCount(device.Id));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_NullCollectionIsQuarantinedAsCorruptState()
    {
        var folder = TemporaryFolder();
        try
        {
            var path = Path.Combine(folder, "monitor.json");
            File.WriteAllText(path, """
            {
              "SchemaVersion": 2,
              "Baselines": null,
              "ActiveFailures": {},
              "Capabilities": {},
              "Events": []
            }
            """);

            var store = new ViewerMonitoringStore(path);

            Assert.Empty(store.LoadEvents());
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(folder, "monitor.json.corrupt-*"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_ReadIoFailurePropagatesWithoutQuarantining()
    {
        var persistence = new TestMonitoringPersistence
        {
            ReadException = new UnauthorizedAccessException("simulated access denial")
        };

        Assert.Throws<UnauthorizedAccessException>(
            () => new ViewerMonitoringStore("monitor.json", persistence));
        Assert.Equal(0, persistence.QuarantineCount);
        Assert.Equal(0, persistence.WriteCount);
    }

    [Fact]
    public void MonitoringStore_SaveFailureRollsBackFailureAndSequence()
    {
        var persistence = new TestMonitoringPersistence
        {
            WriteException = new IOException("simulated write failure")
        };
        var store = new ViewerMonitoringStore("monitor.json", persistence);
        var device = Profile();

        Assert.Throws<IOException>(() => store.RecordFailure(device, "TCP_TIMEOUT"));
        Assert.Null(store.GetActiveFailureCode(device.Id));
        Assert.Empty(store.LoadEvents());

        persistence.WriteException = null;
        var created = Assert.Single(store.RecordFailure(device, "TCP_TIMEOUT"));

        Assert.Equal(1, created.Sequence);
        Assert.Equal("TCP_TIMEOUT", store.GetActiveFailureCode(device.Id));
    }

    [Fact]
    public void MonitoringStore_SaveFailureDoesNotAdvanceOutputBaseline()
    {
        var persistence = new TestMonitoringPersistence();
        var store = new ViewerMonitoringStore("monitor.json", persistence);
        var device = Profile();
        Assert.Empty(store.RecordOutput(
            device,
            "show port status",
            PortStatus(("1", "Up"))));
        var persistedBaseline = persistence.Content;

        persistence.WriteException = new IOException("simulated write failure");
        Assert.Throws<IOException>(
            () => store.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Down"))));

        Assert.Equal(persistedBaseline, persistence.Content);
        Assert.Empty(store.LoadEvents());
        Assert.Equal(0, store.GetActiveInterfaceConditionCount(device.Id));

        persistence.WriteException = null;
        var created = Assert.Single(
            store.RecordOutput(
                device,
                "show port status",
                PortStatus(("1", "Down"))));
        Assert.Equal(1, created.Sequence);
    }

    [Fact]
    public void MonitoringStore_SyslogDiffHandlesSubsetDuplicatesReorderingAndAdditions()
    {
        var folder = TemporaryFolder();
        try
        {
            var device = Profile();

            var subsetStore = Store("subset");
            Assert.Empty(subsetStore.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((1, "line-a"), (2, "line-b"))));
            Assert.Empty(subsetStore.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((2, "line-b"))));

            var duplicateStore = Store("duplicate");
            Assert.Empty(duplicateStore.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((1, "line-a"), (2, "line-b"))));
            var duplicate = duplicateStore.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((1, "line-a"), (2, "line-b"), (2, "line-b")));
            var duplicateEvent = Assert.Single(duplicate);
            Assert.Equal("새 로그", duplicateEvent.Kind);
            Assert.Equal("새 시스템 로그 1건", duplicateEvent.Title);

            var reorderStore = Store("reorder");
            Assert.Empty(reorderStore.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((1, "line-a"), (2, "line-b"), (3, "line-c"))));
            Assert.Empty(reorderStore.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((3, "line-c"), (1, "line-a"), (2, "line-b"))));

            var additionStore = Store("addition");
            Assert.Empty(additionStore.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((1, "line-a"), (2, "line-b"))));
            var additions = additionStore.RecordOutput(
                device,
                "show sylog tail num 100",
                Syslog((3, "line-c"), (2, "line-b"), (4, "line-d"), (1, "line-a")));
            var additionEvent = Assert.Single(additions);
            Assert.Equal("새 로그", additionEvent.Kind);
            Assert.Equal("새 시스템 로그 2건", additionEvent.Title);

            var fallbackStore = Store("show-log-ram");
            Assert.Empty(fallbackStore.RecordOutput(
                device,
                "show log ram",
                Syslog((1, "line-a"))));
            var fallbackAddition = Assert.Single(fallbackStore.RecordOutput(
                device,
                "show log ram",
                Syslog((1, "line-a"), (2, "line-b"))));
            Assert.Equal("새 시스템 로그 1건", fallbackAddition.Title);

            ViewerMonitoringStore Store(string name) =>
                new(Path.Combine(folder, $"monitor-{name}.json"));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void MonitoringStore_SyslogResetOrRotationReportsStateWithoutFalseNewLog()
    {
        var folder = TemporaryFolder();
        try
        {
            var store = new ViewerMonitoringStore(Path.Combine(folder, "monitor.json"));
            var device = Profile();

            Assert.Empty(store.RecordOutput(
                device,
                "show syslog tail num 100",
                Syslog((1, "line-a"), (2, "line-b"))));
            var rotation = Assert.Single(store.RecordOutput(
                device,
                "show syslog tail num 100",
                Syslog((3, "line-c"), (4, "line-d"))));
            Assert.Equal("로그 상태", rotation.Kind);
            Assert.Equal("로그 버퍼 순환 또는 초기화 감지", rotation.Title);

            var afterRotation = store.RecordOutput(
                device,
                "show syslog tail num 100",
                Syslog((5, "line-e"), (3, "line-c"), (4, "line-d")));
            var item = Assert.Single(afterRotation);
            Assert.Equal("새 시스템 로그 1건", item.Title);

            var cleared = Assert.Single(store.RecordOutput(
                device,
                "show syslog tail num 100",
                "No syslog entries."));
            Assert.Equal("로그 상태", cleared.Kind);
            Assert.Empty(store.RecordOutput(
                device,
                "show syslog tail num 100",
                Syslog((6, "line-f"))));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void AgentV4Mapper_RequiresIdentityAndMapsRawCommandResults()
    {
        var identity = AgentContractMapper.MapIdentityV4("""
        {
          "apiVersion":4,
          "agentId":"agent-a",
          "instanceId":"instance-a",
          "certificatePublicKeySha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
          "protocol":"https",
          "maxCommandsPerRequest":8,
          "maxOutputBytes":65536
        }
        """);
        var result = AgentContractMapper.MapTelnetExecutionResultV4("""
        {
          "apiVersion":4,
          "requestId":"request-a",
          "success":true,
          "privilege":"privileged",
          "promptTerminator":"#",
          "startedUtc":"2026-07-23T01:00:00Z",
          "completedUtc":"2026-07-23T01:00:01Z",
          "durationMs":1000,
          "sessionCount":1,
          "reconnectCount":0,
          "commands":[{
            "command":"show running-config",
            "output":"raw-result",
            "truncated":false,
            "collectedUtc":"2026-07-23T01:00:01Z"
          }]
        }
        """);

        Assert.Equal(4, identity.ApiVersion);
        Assert.Equal("agent-a", identity.AgentId);
        Assert.Equal("raw-result", Assert.Single(result.Commands).Output);
        Assert.Equal("#", result.PromptTerminator);
    }

    [Fact]
    public void CertificateTrust_IsAutomaticAndBlocksChangedAgentKey()
    {
        using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var first = CreateCertificate(firstKey, "CN=agent-a");
        using var second = CreateCertificate(secondKey, "CN=agent-a");
        var settings = new ViewerSettings { AgentUri = "https://agent-a:18443" };
        var initial = new CertificatePinValidator(settings);

        Assert.True(initial.Validate(new HttpRequestMessage(), first, null, SslPolicyErrors.RemoteCertificateChainErrors));
        var firstPin = CertificatePinValidator.GetSpkiSha256(first);
        Assert.True(initial.CompleteTrust(firstPin));
        Assert.True(settings.TryGetAgentTrustPin(out var stored));
        Assert.Equal(firstPin, stored);

        var changed = new CertificatePinValidator(settings);
        Assert.False(changed.Validate(new HttpRequestMessage(), second, null, SslPolicyErrors.None));
        Assert.True(changed.IdentityChanged);
    }

    [Fact]
    public void CurrentUserDpapi_RoundTripsWithoutReturningPlainText()
    {
        if (!OperatingSystem.IsWindows()) return;
        var protector = new CurrentUserSecretProtector();
        const string secret = "do-not-store-plain";

        var encrypted = protector.Protect(secret);

        Assert.DoesNotContain(secret, encrypted, StringComparison.Ordinal);
        Assert.Equal(secret, protector.Unprotect(encrypted));
    }

    [Fact]
    public async Task ManualQuery_SendsTargetAndCredentialsOnEveryRequestAndKeepsRawOutputInMemoryOnly()
    {
        var folder = TemporaryFolder();
        try
        {
            var devicePath = Path.Combine(folder, "devices.json");
            var monitorPath = Path.Combine(folder, "monitor.json");
            var settingsPath = Path.Combine(folder, "settings.json");
            var devices = new ManagedDeviceStore(devicePath, new TestProtector());
            var draft = Draft("login-secret", "enable-secret");
            draft.ConnectionVerified = true;
            draft.LastConnectionTestUtc = DateTimeOffset.UtcNow;
            draft.LastConnectionTestCode = "OK";
            var saved = devices.Save(draft);
            var client = new StatelessFakeClient();
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(settingsPath),
                new StatelessFactory(client),
                deviceStore: devices,
                monitoringStore: new ViewerMonitoringStore(monitorPath));
            try
            {
                await viewModel.InitializeAsync();
                viewModel.SelectedDevice = Assert.Single(viewModel.Devices);
                viewModel.ReadOnlyQueryCommand = "show running-config";

                viewModel.ExecuteReadOnlyQueryCommand.Execute(null);
                await WaitUntilAsync(() => !viewModel.IsReadOnlyQueryRunning && client.LastRequest is not null);

                var request = Assert.IsType<TelnetExecuteRequestDto>(client.LastRequest);
                Assert.Equal(saved.Host, request.Host);
                Assert.Equal("operator", request.Username);
                Assert.Equal("login-secret", request.Password);
                Assert.Equal("enable-secret", request.EnablePassword);
                Assert.Equal(["show running-config"], request.Commands);
                Assert.Equal("sensitive raw output", viewModel.ReadOnlyQueryOutput);
                Assert.DoesNotContain("sensitive raw output", File.ReadAllText(devicePath), StringComparison.Ordinal);
                Assert.DoesNotContain("sensitive raw output", File.ReadAllText(monitorPath), StringComparison.Ordinal);
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
    public async Task InitialAgentFailure_StillLoadsViewerDevicesAndManualRefreshRecovers()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = new ManagedDeviceStore(Path.Combine(folder, "devices.json"), new TestProtector());
            devices.Save(Draft("login-secret", null));
            var client = new RecoveringStatelessClient(startFailures: 1);
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
                new RecoveringStatelessFactory(client),
                deviceStore: devices);
            try
            {
                await viewModel.InitializeAsync();

                Assert.Single(viewModel.Devices);
                Assert.True(viewModel.ReadOnlyQueriesEnabled);
                Assert.Equal(AgentConnectionState.Offline, viewModel.HttpConnectionState);
                Assert.Null(viewModel.LastSuccessfulReceiptAt);

                viewModel.RefreshCommand.Execute(null);
                await WaitUntilAsync(() =>
                    !viewModel.IsBusy
                    && client.SuccessfulStarts == 1
                    && viewModel.HttpConnectionState == AgentConnectionState.Demo);

                Assert.Single(viewModel.Devices);
                Assert.NotNull(viewModel.LastSuccessfulReceiptAt);
                Assert.Contains("연결 확인 완료", viewModel.OperationMessage, StringComparison.Ordinal);
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
    public async Task AuthenticationFailure_BlocksFurtherMonitoringEvenWhenPersistenceFails()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = new ThrowingConnectionTestStore(
                Path.Combine(folder, "devices.json"),
                new TestProtector());
            var draft = Draft("login-secret", null);
            draft.ConnectionVerified = true;
            draft.MonitoringEnabled = true;
            draft.LastConnectionTestUtc = DateTimeOffset.UtcNow;
            draft.LastConnectionTestCode = "OK";
            var saved = devices.Save(draft);
            var client = new AuthenticationFailureClient();
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
                new AuthenticationFailureFactory(client),
                deviceStore: devices,
                monitoringStore: new ViewerMonitoringStore(Path.Combine(folder, "monitor.json")));
            try
            {
                await viewModel.InitializeAsync();
                await WaitUntilAsync(() =>
                    viewModel.IsMonitoringCredentialBlocked(saved.Id)
                    && viewModel.OperationMessage.Contains("설정 파일 저장은 실패", StringComparison.Ordinal));

                Assert.Equal(1, client.ExecuteCount);
                Assert.True(Assert.Single(devices.Load()).MonitoringEnabled);
                Assert.Contains("설정 파일 저장은 실패", viewModel.OperationMessage, StringComparison.Ordinal);

                await viewModel.RunMonitoringCycleAsync();

                Assert.Equal(1, client.ExecuteCount);

                var verified = devices.CreateEditDraft(saved.Id);
                verified.ConnectionVerified = true;
                verified.MonitoringEnabled = true;
                verified.LastConnectionTestUtc = DateTimeOffset.UtcNow.AddSeconds(1);
                verified.LastConnectionTestCode = "OK";
                viewModel.SaveManagedDevice(verified);

                Assert.False(viewModel.IsMonitoringCredentialBlocked(saved.Id));
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
    public async Task AuthenticationFailure_BlocksBeforeMonitoringStateWrite()
    {
        var folder = TemporaryFolder();
        try
        {
            var devices = new ThrowingConnectionTestStore(
                Path.Combine(folder, "devices.json"),
                new TestProtector());
            var draft = Draft("login-secret", null);
            draft.ConnectionVerified = true;
            draft.MonitoringEnabled = true;
            draft.LastConnectionTestUtc = DateTimeOffset.UtcNow;
            draft.LastConnectionTestCode = "OK";
            var saved = devices.Save(draft);
            var monitoringPersistence = new TestMonitoringPersistence
            {
                WriteExceptionAfterSuccessfulWrites =
                    new IOException("simulated monitoring state write failure")
            };
            var client = new AuthenticationFailureClient();
            var viewModel = new DashboardViewModel(
                new ViewerSettings { DemoMode = true },
                new ViewerSettingsStore(Path.Combine(folder, "settings.json")),
                new AuthenticationFailureFactory(client),
                deviceStore: devices,
                monitoringStore: new ViewerMonitoringStore(
                    Path.Combine(folder, "monitor.json"),
                    monitoringPersistence));
            try
            {
                await viewModel.InitializeAsync();
                await WaitUntilAsync(() =>
                    viewModel.IsMonitoringCredentialBlocked(saved.Id)
                    && viewModel.OperationMessage.Contains(
                        "VIEWER_MONITOR_STATE_WRITE_FAILED",
                        StringComparison.Ordinal));

                Assert.Equal(1, client.ExecuteCount);
                Assert.True(Assert.Single(devices.Load()).MonitoringEnabled);

                await viewModel.RunMonitoringCycleSafelyAsync(CancellationToken.None);

                Assert.Contains("인증 실패", viewModel.OperationMessage, StringComparison.Ordinal);
                Assert.Contains(
                    "VIEWER_MONITOR_STATE_WRITE_FAILED",
                    viewModel.OperationMessage,
                    StringComparison.Ordinal);

                monitoringPersistence.WriteExceptionAfterSuccessfulWrites = null;
                await viewModel.RunMonitoringCycleAsync();

                Assert.Equal(1, client.ExecuteCount);
            }
            finally
            {
                monitoringPersistence.WriteExceptionAfterSuccessfulWrites = null;
                await viewModel.DisposeAsync();
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static ManagedDeviceDraft Draft(string password, string? enablePassword) => new()
    {
        DisplayName = "ACCESS-SW-01",
        Model = "IES4224GP",
        Host = "192.0.2.10",
        Username = "operator",
        Password = password,
        EnablePassword = enablePassword ?? string.Empty
    };

    private static ManagedDeviceProfile Profile() => new()
    {
        Id = "sw-01",
        DisplayName = "ACCESS-SW-01",
        Model = "IES4224GP",
        Host = "192.0.2.10",
        Port = 23,
        ProtectedUsername = "protected",
        ProtectedPassword = "protected",
        ConnectionVerified = true,
        MonitoringEnabled = true
    };

    private static string PortStatus(params (string PortId, string Link)[] ports)
    {
        var lines = new List<string> { "Port Admin Link Speed Duplex" };
        lines.AddRange(ports.Select(port =>
            $"{port.PortId} Enabled {port.Link} 1000M Full"));
        return string.Join("\r\n", lines);
    }

    private static string Syslog(params (int Sequence, string Message)[] entries) =>
        string.Join(
            "\r\n",
            entries.Select(entry =>
                $"[{entry.Sequence}] 00:00:{entry.Sequence:00} 2026-07-23\r\n" +
                $"\"{entry.Message}\"\r\n" +
                "level: 6, module: 6, function: 1, and event no.: 1"));

    private static string TemporaryFolder()
    {
        var path = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-ViewerManaged", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static X509Certificate2 CreateCertificate(ECDsa key, string subject)
    {
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class TestProtector : IViewerSecretProtector
    {
        public string Protect(string plainText) =>
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("protected:" + plainText));

        public string Unprotect(string protectedText)
        {
            var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
            return decoded["protected:".Length..];
        }
    }

    private sealed class TestManagedDevicePersistence : IManagedDevicePersistence
    {
        public string? Content { get; set; }
        public Exception? ReadException { get; init; }
        public Exception? WriteException { get; set; }
        public Exception? QuarantineException { get; init; }
        public int WriteCount { get; private set; }
        public int QuarantineCount { get; private set; }

        public string? ReadIfExists(string path)
        {
            if (ReadException is not null) throw ReadException;
            return Content;
        }

        public void WriteAtomically(string path, string content)
        {
            WriteCount++;
            if (WriteException is not null) throw WriteException;
            Content = content;
        }

        public void Quarantine(string path, string destination)
        {
            QuarantineCount++;
            if (QuarantineException is not null) throw QuarantineException;
            Content = null;
        }
    }

    private sealed class TestMonitoringPersistence : IViewerMonitoringPersistence
    {
        public string? Content { get; private set; }
        public Exception? ReadException { get; init; }
        public Exception? WriteException { get; set; }
        public Exception? WriteExceptionAfterSuccessfulWrites { get; set; }
        public int WriteCount { get; private set; }
        public int QuarantineCount { get; private set; }

        public string? ReadIfExists(string path)
        {
            if (ReadException is not null) throw ReadException;
            return Content;
        }

        public void WriteAtomically(string path, string content)
        {
            WriteCount++;
            if (WriteException is not null) throw WriteException;
            if (WriteExceptionAfterSuccessfulWrites is not null && WriteCount > 1)
            {
                throw WriteExceptionAfterSuccessfulWrites;
            }
            Content = content;
        }

        public void Quarantine(string path, string destination)
        {
            QuarantineCount++;
            Content = null;
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (!condition() && DateTimeOffset.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class StatelessFactory(StatelessFakeClient client) : IAgentClientFactory
    {
        public IAgentClient Create(ViewerSettings settings) => client;
    }

    private sealed class StatelessFakeClient : IAgentClient
    {
        public TelnetExecuteRequestDto? LastRequest { get; private set; }
        public bool SupportsStatelessV4 => true;
        public event EventHandler<AgentEventChangeDto>? EventChanged { add { } remove { } }
        public event EventHandler<AgentConnectionState>? ConnectionStateChanged;
        public Task StartAsync(CancellationToken cancellationToken)
        {
            ConnectionStateChanged?.Invoke(this, AgentConnectionState.Demo);
            return Task.CompletedTask;
        }
        public Task<AgentIdentityDto> GetIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgentIdentityDto(4, "fake", "fake-instance", new string('A', 64), "https", 8, 65_536));
        public Task<TelnetExecutionResultDto> TestTelnetAsync(TelnetTargetDto target, CancellationToken cancellationToken) =>
            Task.FromResult(Result(target.RequestId, []));
        public Task<TelnetExecutionResultDto> ExecuteTelnetAsync(TelnetExecuteRequestDto request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Result(request.RequestId,
            [
                new TelnetCommandOutputDto(request.Commands[0], "sensitive raw output", false, DateTimeOffset.UtcNow)
            ]));
        }
        public Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromException<AgentSnapshotDto>(new NotSupportedException());
        public Task<IReadOnlyList<SwitchEventDto>> GetRecentEventsAsync(int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SwitchEventDto>>([]);
        public Task<EventChangePageDto> GetEventChangesAsync(long cursor, int limit, CancellationToken cancellationToken) =>
            Task.FromResult(new EventChangePageDto(cursor, cursor, false, []));
        public Task<CommandResultDto> ExecuteRegisteredCheckAsync(string deviceId, string commandId, CancellationToken cancellationToken) =>
            Task.FromResult(new CommandResultDto(false, "not used"));
        public Task<ReadOnlyQueryResultDto> ExecuteReadOnlyQueryAsync(string deviceId, string command, CancellationToken cancellationToken) =>
            Task.FromException<ReadOnlyQueryResultDto>(new NotSupportedException());
        public Task<bool> AcknowledgeAsync(string eventId, CancellationToken cancellationToken) => Task.FromResult(false);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static TelnetExecutionResultDto Result(
            string requestId,
            IReadOnlyList<TelnetCommandOutputDto> commands)
        {
            var now = DateTimeOffset.UtcNow;
            return new TelnetExecutionResultDto(4, requestId, true, "privileged", "#", now, now, 1, commands);
        }
    }

    private sealed class ThrowingConnectionTestStore(string path, IViewerSecretProtector protector)
        : ManagedDeviceStore(path, protector)
    {
        public override ManagedDeviceProfile MarkConnectionTest(string id, bool success, string code) =>
            throw new IOException("simulated write failure");
    }

    private sealed class RecoveringStatelessFactory(RecoveringStatelessClient client) : IAgentClientFactory
    {
        public IAgentClient Create(ViewerSettings settings) => client;
    }

    private sealed class RecoveringStatelessClient(int startFailures) : StatelessClientBase
    {
        private int _remainingStartFailures = startFailures;
        public int SuccessfulStarts { get; private set; }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Decrement(ref _remainingStartFailures) >= 0)
            {
                RaiseConnectionState(AgentConnectionState.Offline);
                throw new AgentClientException("AGENT_UNREACHABLE", AgentConnectionState.Offline);
            }
            SuccessfulStarts++;
            RaiseConnectionState(AgentConnectionState.Demo);
            return Task.CompletedTask;
        }
    }

    private sealed class AuthenticationFailureFactory(AuthenticationFailureClient client) : IAgentClientFactory
    {
        public IAgentClient Create(ViewerSettings settings) => client;
    }

    private sealed class AuthenticationFailureClient : StatelessClientBase
    {
        public int ExecuteCount { get; private set; }

        public override Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken)
        {
            ExecuteCount++;
            throw new AgentClientException("AUTH_FAILED", AgentConnectionState.Stale);
        }
    }

    private abstract class StatelessClientBase : IAgentClient
    {
        public bool SupportsStatelessV4 => true;
        public event EventHandler<AgentEventChangeDto>? EventChanged { add { } remove { } }
        public event EventHandler<AgentConnectionState>? ConnectionStateChanged;

        public virtual Task StartAsync(CancellationToken cancellationToken)
        {
            RaiseConnectionState(AgentConnectionState.Demo);
            return Task.CompletedTask;
        }

        protected void RaiseConnectionState(AgentConnectionState state) =>
            ConnectionStateChanged?.Invoke(this, state);

        public Task<AgentIdentityDto> GetIdentityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new AgentIdentityDto(4, "fake", "fake-instance", new string('A', 64), "https", 8, 65_536));

        public Task<TelnetExecutionResultDto> TestTelnetAsync(
            TelnetTargetDto target,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result(target.RequestId, []));

        public virtual Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
            TelnetExecuteRequestDto request,
            CancellationToken cancellationToken) =>
            Task.FromResult(Result(request.RequestId,
            [
                new TelnetCommandOutputDto(request.Commands[0], "output", false, DateTimeOffset.UtcNow)
            ]));

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

        private static TelnetExecutionResultDto Result(
            string requestId,
            IReadOnlyList<TelnetCommandOutputDto> commands)
        {
            var now = DateTimeOffset.UtcNow;
            return new TelnetExecutionResultDto(4, requestId, true, "privileged", "#", now, now, 1, commands);
        }
    }
}
