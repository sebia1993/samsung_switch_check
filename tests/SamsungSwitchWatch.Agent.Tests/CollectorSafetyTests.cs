using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Persistence;
using SamsungSwitchWatch.Agent.Polling;
using SamsungSwitchWatch.Agent.Security;
using SamsungSwitchWatch.Core.Profiles;
using SamsungSwitchWatch.Core.Telnet;

namespace SamsungSwitchWatch.Agent.Tests;

public sealed class CollectorSafetyTests
{
    [Fact]
    public async Task InterfaceCollectionRequiresConfiguredUplinkRow()
    {
        var captured = DateTimeOffset.UtcNow;
        var output = """
            Port      Admin     Link    Speed   Duplex
            1         Enabled   Up      1000M   Full
            """;
        var telnet = new StaticTelnetClient(new TelnetSessionResult(
            "IES4224GP",
            [new CommandOutput("interface_status", "show interfaces status", output, output, captured)],
            captured.AddSeconds(-1),
            captured));
        var collector = new CoreTelnetDeviceCollector(telnet, Ies4224GpProfile.Create(), new StaticCredentialVault());
        var device = new SwitchOptions { UplinkPort = "24" };

        var exception = await Assert.ThrowsAsync<AgentOperationException>(() => collector.CollectAsync(
            device,
            CommandCatalog.Registered["interface_status"],
            CancellationToken.None));

        Assert.Equal(AgentErrorCodes.IncompleteOutput, exception.Code);
    }

    [Fact]
    public async Task UnsupportedCollectorStatusDoesNotReplaceLastGoodSnapshot()
    {
        var collector = new UnsupportedStatusCollector();
        await using var host = await TestAgentHost.StartAsync(collector);
        await host.PairAsync();
        var store = host.Services.GetRequiredService<SamsungSwitchWatch.Agent.Persistence.SqliteAgentStore>();
        var baseline = new DeviceSnapshot("TEST-SW-01", "version", DateTimeOffset.UtcNow.AddMinutes(-1),
            new JsonObject { ["softwareVersion"] = "LAST-GOOD" });
        await store.UpsertSnapshotAsync(baseline);

        using var response = await host.Client.PostAsync("/api/v1/commands/TEST-SW-01/version", null);

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var current = await store.GetSnapshotAsync("TEST-SW-01", "version");
        Assert.NotNull(current);
        Assert.Equal("LAST-GOOD", current.Data["softwareVersion"]?.GetValue<string>());
    }

    [Fact]
    public async Task CoreCollectorExecutesSeveralRegisteredCommandsInOneTelnetSession()
    {
        var telnet = new CapturingTelnetClient();
        var collector = new CoreTelnetDeviceCollector(telnet, Ies4224GpProfile.Create(), new StaticCredentialVault());
        var commands = CommandCatalog.Registered.Values.ToArray();

        var outputs = await collector.CollectBatchAsync(new SwitchOptions { UplinkPort = "24" }, commands,
            CancellationToken.None);

        Assert.Equal(1, telnet.Calls);
        Assert.Equal(commands.Select(command => command.Id), telnet.LastCommandIds);
        Assert.Equal(commands.Length, outputs.Count);
        Assert.All(outputs, output => Assert.Equal("OK", output.CollectorStatus));
    }

    [Fact]
    public async Task CoreCollectorKeepsUnsupportedParserResultIsolatedToItsCommand()
    {
        var captured = DateTimeOffset.UtcNow;
        var telnet = new StaticTelnetClient(new TelnetSessionResult("IES4224GP",
            [
                new CommandOutput("version", "show version",
                    "Model Name : IES4224GP\nSoftware Version : TEST",
                    "Model Name : IES4224GP\nSoftware Version : TEST", captured),
                new CommandOutput("system", "show system", "unrecognized sanitized output",
                    "unrecognized sanitized output", captured)
            ], captured.AddSeconds(-1), captured));
        var collector = new CoreTelnetDeviceCollector(telnet, Ies4224GpProfile.Create(), new StaticCredentialVault());

        var outputs = await collector.CollectBatchAsync(new SwitchOptions(),
            [CommandCatalog.Registered["version"], CommandCatalog.Registered["system"]], CancellationToken.None);

        Assert.Equal("OK", outputs.Single(output => output.CommandId == "version").CollectorStatus);
        Assert.Equal(AgentErrorCodes.ParserUnsupported,
            outputs.Single(output => output.CommandId == "system").CollectorStatus);
    }

    [Fact]
    public async Task CommandExecutionBatchUsesCollectorBatchBoundary()
    {
        var collector = new BatchOnlyCollector();
        await using var host = await TestAgentHost.StartAsync(collector);
        var execution = host.Services.GetRequiredService<CommandExecutionService>();

        var results = await execution.ExecuteBatchAsync("TEST-SW-01", ["version", "system"],
            "test", CancellationToken.None);

        Assert.Equal(1, collector.BatchCalls);
        Assert.Equal(0, collector.SingleCalls);
        Assert.All(results, result => Assert.True(result.Success));
    }

    [Fact]
    public async Task RebootBatchCreatesOneRestartEventWithoutSeparateLogReset()
    {
        await using var host = await TestAgentHost.StartAsync(new RebootBatchCollector());
        var execution = host.Services.GetRequiredService<CommandExecutionService>();
        var store = host.Services.GetRequiredService<SqliteAgentStore>();

        await execution.ExecuteBatchAsync("TEST-SW-01", ["system", "log_ram"],
            "test", CancellationToken.None);
        await execution.ExecuteBatchAsync("TEST-SW-01", ["system", "log_ram"],
            "test", CancellationToken.None);

        var events = await store.GetEventsAfterAsync(0);
        var restart = Assert.Single(events);
        Assert.Equal("device-restart", restart.Type);
        Assert.DoesNotContain(events, item => item.Type == "log-buffer-reset");
    }

    [Fact]
    public async Task ParserUnsupportedIsOneCommandWarningAndNeverBecomesCriticalFailure()
    {
        await using var host = await TestAgentHost.StartAsync(new UnsupportedStatusCollector());
        var execution = host.Services.GetRequiredService<CommandExecutionService>();
        var store = host.Services.GetRequiredService<SqliteAgentStore>();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var exception = await Assert.ThrowsAsync<AgentOperationException>(() => execution.ExecuteAsync(
                "TEST-SW-01", "version", "test", CancellationToken.None));
            Assert.Equal(AgentErrorCodes.ParserUnsupported, exception.Code);
        }

        var warning = Assert.Single(await store.GetEventsAfterAsync(0));
        Assert.Equal("collector-command-unsupported", warning.Type);
        Assert.Equal(EventSeverity.Warning, warning.Severity);
        Assert.True(warning.IsActiveCondition);
        var commandHealth = await store.GetSnapshotAsync(
            "TEST-SW-01", CommandCatalog.CollectorHealthSnapshotIdFor("version"));
        var aggregateHealth = await store.GetSnapshotAsync("TEST-SW-01", CommandCatalog.CollectorHealthSnapshotId);
        Assert.Equal("Unsupported", commandHealth!.Data["state"]?.GetValue<string>());
        Assert.Equal(1, commandHealth.Data["consecutiveFailures"]?.GetValue<int>());
        Assert.Equal("Unsupported", aggregateHealth!.Data["state"]?.GetValue<string>());
        Assert.Equal(0, (await store.GetEventSummaryAsync()).ActiveCritical);
    }

    [Fact]
    public async Task IncompleteOutputStillCreatesCriticalFailureAtThirdAttempt()
    {
        await using var host = await TestAgentHost.StartAsync(new IncompleteStatusCollector());
        var execution = host.Services.GetRequiredService<CommandExecutionService>();
        var store = host.Services.GetRequiredService<SqliteAgentStore>();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var exception = await Assert.ThrowsAsync<AgentOperationException>(() => execution.ExecuteAsync(
                "TEST-SW-01", "version", "test", CancellationToken.None));
            Assert.Equal(AgentErrorCodes.IncompleteOutput, exception.Code);
        }

        var failure = Assert.Single(await store.GetEventsAfterAsync(0));
        Assert.Equal("collector-failed", failure.Type);
        Assert.Equal(EventSeverity.Critical, failure.Severity);
        Assert.Equal(AgentErrorCodes.IncompleteOutput, failure.Details["errorCode"]);
        var health = await store.GetSnapshotAsync(
            "TEST-SW-01", CommandCatalog.CollectorHealthSnapshotIdFor("version"));
        Assert.Equal("Failed", health!.Data["state"]?.GetValue<string>());
        Assert.Equal(3, health.Data["consecutiveFailures"]?.GetValue<int>());
    }

    [Fact]
    public async Task AuthenticationRecoveryIsCreatedOnlyOnceForMultiCommandBatch()
    {
        await using var host = await TestAgentHost.StartAsync(new BatchOnlyCollector());
        var execution = host.Services.GetRequiredService<CommandExecutionService>();
        var store = host.Services.GetRequiredService<SqliteAgentStore>();
        var options = host.Services.GetRequiredService<AgentOptions>();
        var captured = DateTimeOffset.UtcNow.AddMinutes(-1);
        await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Critical,
            "collector-auth-blocked", "Authentication blocked", "Sanitized test fixture.", EventState.New,
            "collector-auth:TEST-SW-01", IsActiveCondition: true));
        await store.UpsertSnapshotAsync(new DeviceSnapshot("TEST-SW-01",
            CommandCatalog.CollectorAuthCircuitSnapshotId, captured, new JsonObject
            {
                ["blocked"] = true,
                ["errorCode"] = AgentErrorCodes.AuthFailed,
                ["credentialVersion"] = "missing"
            }));
        var credentialFolder = Path.Combine(options.DataDirectory, "credentials");
        Directory.CreateDirectory(credentialFolder);
        await File.WriteAllBytesAsync(Path.Combine(credentialFolder, "test-switch-readonly.bin"),
            "changed-test-envelope"u8.ToArray());

        var results = await execution.ExecuteBatchAsync("TEST-SW-01", ["version", "system"],
            "test", CancellationToken.None);

        Assert.All(results, result => Assert.True(result.Success));
        var events = await store.GetEventsAfterAsync(0);
        Assert.Equal(1, events.Count(item => item.Type == "collector-auth-recovered"));
        Assert.Equal(EventState.Recovered, events.Single(item => item.Type == "collector-auth-blocked").State);
        Assert.Equal(0, (await store.GetEventSummaryAsync()).ActiveCritical);
    }

    [Fact]
    public void ParserUnsupportedUsesHourlyRetryWhileIncompleteUsesTransientBackoff()
    {
        Assert.Equal(TimeSpan.FromHours(1), PollBackoffPolicy.ForError(AgentErrorCodes.ParserUnsupported, 1));
        Assert.Equal(TimeSpan.FromSeconds(60), PollBackoffPolicy.ForError(AgentErrorCodes.IncompleteOutput, 3));
    }

    private sealed class StaticTelnetClient(TelnetSessionResult result) : ITelnetClient
    {
        public Task<TelnetSessionResult> ExecuteRegisteredAsync(
            TelnetEndpoint endpoint,
            TelnetCredentials credentials,
            DeviceCommandProfile profile,
            IReadOnlyCollection<string> commandIds,
            CancellationToken cancellationToken = default) => Task.FromResult(result);
    }

    private sealed class StaticCredentialVault : ICredentialVault
    {
        public Task StoreAsync(string credentialId, SwitchCredential credential, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<SwitchCredential?> GetAsync(string credentialId, CancellationToken cancellationToken = default) =>
            Task.FromResult<SwitchCredential?>(new SwitchCredential("readonly", "test-only"));
    }

    private sealed class CapturingTelnetClient : ITelnetClient
    {
        public int Calls { get; private set; }
        public IReadOnlyList<string> LastCommandIds { get; private set; } = [];

        public Task<TelnetSessionResult> ExecuteRegisteredAsync(
            TelnetEndpoint endpoint,
            TelnetCredentials credentials,
            DeviceCommandProfile profile,
            IReadOnlyCollection<string> commandIds,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastCommandIds = commandIds.ToArray();
            var captured = DateTimeOffset.UtcNow;
            var outputs = commandIds.Select(id =>
            {
                var text = id switch
                {
                    "version" => "Model Name : IES4224GP\nSoftware Version : TEST",
                    "system" => "System Up Time : 0 days 01:00:00\nBoot POST Check : PASS",
                    "log_ram" => "[1] 00:01:00 2026-07-20\n\"Sanitized log\"\nlevel: 6, module: 6, function: 1, and event no.: 1",
                    "interface_status" => "Port Admin Link Speed Duplex\n1 Enabled Up 1000M Full\n24 Enabled Up 1000M Full",
                    _ => throw new InvalidOperationException()
                };
                return new CommandOutput(id, profile.GetRequiredCommand(id).Command, text, text, captured);
            }).ToArray();
            return Task.FromResult(new TelnetSessionResult("IES4224GP", outputs, captured.AddSeconds(-1), captured));
        }
    }

    private sealed class BatchOnlyCollector : IDeviceCollector
    {
        public int SingleCalls { get; private set; }
        public int BatchCalls { get; private set; }

        public Task<CollectedOutput> CollectAsync(
            SwitchOptions device,
            CommandDefinition command,
            CancellationToken cancellationToken)
        {
            SingleCalls++;
            throw new InvalidOperationException("The batch path must be used.");
        }

        public Task<IReadOnlyList<CollectedOutput>> CollectBatchAsync(
            SwitchOptions device,
            IReadOnlyList<CommandDefinition> commands,
            CancellationToken cancellationToken)
        {
            BatchCalls++;
            var now = DateTimeOffset.UtcNow;
            IReadOnlyList<CollectedOutput> outputs = commands.Select(command => new CollectedOutput(
                device.Id,
                command.Id,
                now,
                command.Id == "version"
                    ? new JsonObject { ["model"] = "IES4224GP", ["softwareVersion"] = "TEST" }
                    : new JsonObject { ["uptimeSeconds"] = 3600L },
                "Sanitized batch fixture")).ToArray();
            return Task.FromResult(outputs);
        }
    }

    private sealed class RebootBatchCollector : IDeviceCollector
    {
        private int _batchNumber;

        public Task<CollectedOutput> CollectAsync(
            SwitchOptions device,
            CommandDefinition command,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The reboot fixture requires a batch.");

        public Task<IReadOnlyList<CollectedOutput>> CollectBatchAsync(
            SwitchOptions device,
            IReadOnlyList<CommandDefinition> commands,
            CancellationToken cancellationToken)
        {
            var batchNumber = Interlocked.Increment(ref _batchNumber);
            var captured = DateTimeOffset.UtcNow.AddSeconds(batchNumber);
            var uptime = batchNumber == 1 ? 3600L : 30L;
            var logId = batchNumber == 1 ? "before-reboot" : "after-reboot";
            IReadOnlyList<CollectedOutput> outputs = commands.Select(command => command.Id switch
            {
                "system" => new CollectedOutput(device.Id, command.Id, captured,
                    new JsonObject { ["uptimeSeconds"] = uptime }, "Sanitized system fixture"),
                "log_ram" => new CollectedOutput(device.Id, command.Id, captured,
                    new JsonObject
                    {
                        ["entries"] = new JsonArray(new JsonObject
                        {
                            ["id"] = logId,
                            ["message"] = "Sanitized reboot fixture",
                            ["severity"] = "info"
                        })
                    }, "Sanitized log fixture"),
                _ => throw new InvalidOperationException($"Unexpected command: {command.Id}")
            }).ToArray();
            return Task.FromResult(outputs);
        }
    }

    private sealed class UnsupportedStatusCollector : IDeviceCollector
    {
        public Task<CollectedOutput> CollectAsync(
            SwitchOptions device,
            CommandDefinition command,
            CancellationToken cancellationToken)
        {
            var captured = DateTimeOffset.UtcNow;
            return Task.FromResult(new CollectedOutput(device.Id, command.Id, captured,
                new JsonObject { ["softwareVersion"] = "MUST-NOT-BE-STORED" },
                "Sanitized test fixture",
                AgentErrorCodes.ParserUnsupported));
        }
    }

    private sealed class IncompleteStatusCollector : IDeviceCollector
    {
        public Task<CollectedOutput> CollectAsync(
            SwitchOptions device,
            CommandDefinition command,
            CancellationToken cancellationToken)
        {
            var captured = DateTimeOffset.UtcNow;
            return Task.FromResult(new CollectedOutput(device.Id, command.Id, captured,
                new JsonObject(), "Sanitized test fixture", AgentErrorCodes.IncompleteOutput));
        }
    }
}
