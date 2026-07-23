using System.IO;
using System.Net.Security;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

            var recovered = store.RecordOutput(device, "show port status", "Port 1 Up");
            Assert.Equal(2, recovered.Count);
            Assert.All(recovered, item => Assert.True(item.Recovered));
            Assert.Equal(DeviceHealth.Normal, recovered[^1].Severity);

            Assert.Empty(store.RecordOutput(device, "show sylog tail num 100", "line-a\r\nline-b"));
            Assert.Empty(store.RecordOutput(device, "show sylog tail num 100", "line-b\r\nline-a"));
            var newLog = store.RecordOutput(device, "show sylog tail num 100", "line-c\r\nline-b\r\nline-a");
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
    public void MonitoringStore_SyslogDiffHandlesSubsetDuplicatesReorderingAndAdditions()
    {
        var folder = TemporaryFolder();
        try
        {
            var device = Profile();

            var subsetStore = Store("subset");
            Assert.Empty(subsetStore.RecordOutput(device, "show sylog tail num 100", "line-a\r\nline-b"));
            Assert.Empty(subsetStore.RecordOutput(device, "show sylog tail num 100", "line-b"));

            var duplicateStore = Store("duplicate");
            Assert.Empty(duplicateStore.RecordOutput(device, "show sylog tail num 100", "line-a\r\nline-b"));
            var duplicate = duplicateStore.RecordOutput(
                device,
                "show sylog tail num 100",
                "line-a\r\nline-b\r\nline-b");
            var duplicateEvent = Assert.Single(duplicate);
            Assert.Equal("새 로그", duplicateEvent.Kind);
            Assert.Equal("새 시스템 로그 1건", duplicateEvent.Title);

            var reorderStore = Store("reorder");
            Assert.Empty(reorderStore.RecordOutput(
                device,
                "show sylog tail num 100",
                "line-a\r\nline-b\r\nline-c"));
            Assert.Empty(reorderStore.RecordOutput(
                device,
                "show sylog tail num 100",
                "line-c\r\nline-a\r\nline-b"));

            var additionStore = Store("addition");
            Assert.Empty(additionStore.RecordOutput(device, "show sylog tail num 100", "line-a\r\nline-b"));
            var additions = additionStore.RecordOutput(
                device,
                "show sylog tail num 100",
                "line-c\r\nline-b\r\nline-d\r\nline-a");
            var additionEvent = Assert.Single(additions);
            Assert.Equal("새 로그", additionEvent.Kind);
            Assert.Equal("새 시스템 로그 2건", additionEvent.Title);

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

            Assert.Empty(store.RecordOutput(device, "show syslog tail num 100", "line-a\r\nline-b"));
            var rotation = Assert.Single(store.RecordOutput(
                device,
                "show syslog tail num 100",
                "line-c\r\nline-d"));
            Assert.Equal("로그 상태", rotation.Kind);
            Assert.Equal("로그 버퍼 순환 또는 초기화 감지", rotation.Title);

            var afterRotation = store.RecordOutput(
                device,
                "show syslog tail num 100",
                "line-e\r\nline-c\r\nline-d");
            var item = Assert.Single(afterRotation);
            Assert.Equal("새 시스템 로그 1건", item.Title);

            var cleared = Assert.Single(store.RecordOutput(
                device,
                "show syslog tail num 100",
                string.Empty));
            Assert.Equal("로그 상태", cleared.Kind);
            Assert.Empty(store.RecordOutput(device, "show syslog tail num 100", "line-f"));
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
