using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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
        var collector = new CoreTelnetDeviceCollector(telnet, CreateProfileRegistry(), new StaticCredentialVault());
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
        var collector = new CoreTelnetDeviceCollector(telnet, CreateProfileRegistry(), new StaticCredentialVault());
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
        var collector = new CoreTelnetDeviceCollector(telnet, CreateProfileRegistry(), new StaticCredentialVault());

        var outputs = await collector.CollectBatchAsync(new SwitchOptions(),
            [CommandCatalog.Registered["version"], CommandCatalog.Registered["system"]], CancellationToken.None);

        Assert.Equal("OK", outputs.Single(output => output.CommandId == "version").CollectorStatus);
        Assert.Equal(AgentErrorCodes.ParserUnsupported,
            outputs.Single(output => output.CommandId == "system").CollectorStatus);
    }

    [Theory]
    [InlineData("IES4224GP")]
    [InlineData("IES4028XP")]
    [InlineData("IES4226XP")]
    public async Task CoreCollectorSelectsProfileFromConfiguredDeviceModel(string model)
    {
        var telnet = new CapturingTelnetClient();
        var collector = new CoreTelnetDeviceCollector(telnet, CreateProfileRegistry(),
            new StaticCredentialVault());

        var output = await collector.CollectAsync(new SwitchOptions { Model = model },
            CommandCatalog.Registered[CommandIds.Version], CancellationToken.None);

        Assert.Equal("OK", output.CollectorStatus);
        Assert.Equal(model, telnet.LastProfileModel, ignoreCase: true);
    }

    [Fact]
    public async Task ProfileMissingCommandReportsOnlyThatCapabilityUnsupported()
    {
        var full = Ies4224GpProfile.Create();
        var limited = new DeviceCommandProfile(full.Model, full.Telnet,
            [full.GetRequiredCommand(CommandIds.Version)]);
        var telnet = new CapturingTelnetClient();
        var collector = new CoreTelnetDeviceCollector(telnet, new DeviceProfileRegistry([limited]),
            new StaticCredentialVault());

        var outputs = await collector.CollectBatchAsync(new SwitchOptions(),
            [CommandCatalog.Registered[CommandIds.Version], CommandCatalog.Registered[CommandIds.System]],
            CancellationToken.None);

        Assert.Equal([CommandIds.Version], telnet.LastCommandIds);
        Assert.Equal("OK", outputs.Single(output => output.CommandId == CommandIds.Version).CollectorStatus);
        Assert.Equal(AgentErrorCodes.ParserUnsupported,
            outputs.Single(output => output.CommandId == CommandIds.System).CollectorStatus);
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
    public async Task FirstStatusCreatesCriticalDownWhileFirstLogsRemainBaseline()
    {
        await using var host = await TestAgentHost.StartAsync(new InitialDownCollector());
        var execution = host.Services.GetRequiredService<CommandExecutionService>();
        var store = host.Services.GetRequiredService<SqliteAgentStore>();

        var results = await execution.ExecuteBatchAsync("TEST-SW-01",
            [CommandIds.LogRam, CommandIds.InterfaceStatus], "test", CancellationToken.None);

        Assert.All(results, result => Assert.True(result.Success));
        var active = Assert.Single(await store.GetEventsAfterAsync(0));
        Assert.Equal("uplink-down", active.Type);
        Assert.Equal(EventSeverity.Critical, active.Severity);
        Assert.True(active.IsActiveCondition);
        Assert.Equal(1, (await store.GetEventSummaryAsync()).ActiveCritical);
    }

    [Fact]
    public async Task RebootCorrelationSurvivesSeparateSystemAndLogCollections()
    {
        await using var host = await TestAgentHost.StartAsync(new SplitRebootCollector());
        var execution = host.Services.GetRequiredService<CommandExecutionService>();
        var store = host.Services.GetRequiredService<SqliteAgentStore>();

        await execution.ExecuteAsync("TEST-SW-01", CommandIds.System, "test", CancellationToken.None);
        await execution.ExecuteAsync("TEST-SW-01", CommandIds.LogRam, "test", CancellationToken.None);
        await execution.ExecuteAsync("TEST-SW-01", CommandIds.System, "test", CancellationToken.None);
        await execution.ExecuteAsync("TEST-SW-01", CommandIds.LogRam, "test", CancellationToken.None);

        var events = await store.GetEventsAfterAsync(0);
        Assert.Single(events);
        Assert.Equal("device-restart", events[0].Type);
        Assert.DoesNotContain(events, item => item.Type == "log-buffer-reset");
        var correlation = await store.GetSnapshotAsync("TEST-SW-01",
            CommandCatalog.RebootCorrelationSnapshotId);
        Assert.False(correlation!.Data["pendingLogBaseline"]!.GetValue<bool>());
    }

    [Fact]
    public async Task UnexpectedCollectorFailurePersistsOnlySanitizedFailureMetadata()
    {
        await using var host = await TestAgentHost.StartAsync(new ThrowingCollector());
        var execution = host.Services.GetRequiredService<CommandExecutionService>();
        var store = host.Services.GetRequiredService<SqliteAgentStore>();
        var options = host.Services.GetRequiredService<AgentOptions>();

        var error = await Assert.ThrowsAsync<AgentOperationException>(() => execution.ExecuteAsync(
            "TEST-SW-01", CommandIds.Version, "test", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.PromptParseFailed, error.Code);
        var health = await store.GetSnapshotAsync("TEST-SW-01",
            CommandCatalog.CollectorHealthSnapshotIdFor(CommandIds.Version));
        Assert.Equal(AgentErrorCodes.PromptParseFailed, health!.Data["errorCode"]!.GetValue<string>());
        Assert.Equal("Degraded", health.Data["state"]!.GetValue<string>());

        await using var connection = new SqliteConnection($"Data Source={options.DatabasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT detail FROM audit ORDER BY id DESC LIMIT 1;";
        var detail = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.Equal($"Error code: {AgentErrorCodes.PromptParseFailed}", detail);
        Assert.DoesNotContain("SENSITIVE", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplicateBatchOutputsBecomePersistedIncompleteFailures()
    {
        await using var host = await TestAgentHost.StartAsync(new DuplicateBatchCollector());
        var execution = host.Services.GetRequiredService<CommandExecutionService>();
        var store = host.Services.GetRequiredService<SqliteAgentStore>();

        var results = await execution.ExecuteBatchAsync("TEST-SW-01",
            [CommandIds.Version, CommandIds.System], "test", CancellationToken.None);

        Assert.All(results, result =>
        {
            Assert.False(result.Success);
            Assert.Equal(AgentErrorCodes.IncompleteOutput, result.ErrorCode);
        });
        foreach (var commandId in new[] { CommandIds.Version, CommandIds.System })
        {
            var health = await store.GetSnapshotAsync("TEST-SW-01",
                CommandCatalog.CollectorHealthSnapshotIdFor(commandId));
            Assert.Equal(AgentErrorCodes.IncompleteOutput, health!.Data["errorCode"]!.GetValue<string>());
        }
    }

    [Fact]
    public async Task IntegrityFailureBlocksCollectionBeforeDeviceAccess()
    {
        var collector = new CountingCollector();
        await using var host = await TestAgentHost.StartAsync(collector);
        var execution = host.Services.GetRequiredService<CommandExecutionService>();
        var store = host.Services.GetRequiredService<SqliteAgentStore>();
        var options = host.Services.GetRequiredService<AgentOptions>();
        await using (var connection = new SqliteConnection($"Data Source={options.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM schema_migrations WHERE version=4;";
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        Assert.False(await store.RefreshIntegrityStatusAsync());

        var error = await Assert.ThrowsAsync<AgentOperationException>(() => execution.ExecuteAsync(
            "TEST-SW-01", CommandIds.Version, "test", CancellationToken.None));

        Assert.Equal(AgentErrorCodes.StorageWriteFailed, error.Code);
        Assert.Equal(0, collector.Calls);
    }

    [Fact]
    public async Task PollSchedulerBoundsParallelDevicesAndKeepsOneSessionPerDevice()
    {
        var collector = new ConcurrentBatchCollector(expectedDevices: 3);
        await using var host = await TestAgentHost.StartAsync(collector, MultiDeviceOverrides());
        var hostOptions = host.Services.GetRequiredService<AgentOptions>();
        // Keep the host-owned scheduler disabled. Mutating its shared options during startup can race
        // this test-owned scheduler and make two independent loops poll the same fixture.
        var schedulerOptions = new AgentOptions
        {
            EnablePolling = true,
            MaxConcurrentDevices = 2,
            SchedulerTickSeconds = hostOptions.SchedulerTickSeconds,
            Switches = hostOptions.Switches.ToList()
        };
        var scheduler = new PollSchedulerService(schedulerOptions,
            host.Services.GetRequiredService<CommandExecutionService>(),
            host.Services.GetRequiredService<AgentRuntimeState>(),
            host.Services.GetRequiredService<SqliteAgentStore>(),
            NullLogger<PollSchedulerService>.Instance);

        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            await collector.AllDevicesCompleted.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }

        Assert.Equal(2, collector.MaximumConcurrentDevices);
        Assert.All(collector.MaximumSessionsByDevice.Values, maximum => Assert.Equal(1, maximum));
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
        public string? LastProfileModel { get; private set; }

        public Task<TelnetSessionResult> ExecuteRegisteredAsync(
            TelnetEndpoint endpoint,
            TelnetCredentials credentials,
            DeviceCommandProfile profile,
            IReadOnlyCollection<string> commandIds,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            LastCommandIds = commandIds.ToArray();
            LastProfileModel = profile.Model;
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

    private sealed class InitialDownCollector : IDeviceCollector
    {
        public Task<CollectedOutput> CollectAsync(
            SwitchOptions device,
            CommandDefinition command,
            CancellationToken cancellationToken) =>
            Task.FromResult(Create(device, command));

        public Task<IReadOnlyList<CollectedOutput>> CollectBatchAsync(
            SwitchOptions device,
            IReadOnlyList<CommandDefinition> commands,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CollectedOutput>>(commands.Select(command =>
                Create(device, command)).ToArray());

        private static CollectedOutput Create(SwitchOptions device, CommandDefinition command)
        {
            var captured = DateTimeOffset.UtcNow;
            return command.Id switch
            {
                CommandIds.LogRam => new CollectedOutput(device.Id, command.Id, captured,
                    new JsonObject
                    {
                        ["entries"] = new JsonArray(new JsonObject
                        {
                            ["id"] = "baseline-log",
                            ["message"] = "Sanitized baseline",
                            ["severity"] = "info"
                        })
                    }, "Sanitized log fixture"),
                CommandIds.InterfaceStatus => new CollectedOutput(device.Id, command.Id, captured,
                    new JsonObject
                    {
                        ["uplinkPort"] = device.UplinkPort,
                        ["uplinkAdminUp"] = true,
                        ["uplinkOperationalUp"] = false,
                        ["portsUp"] = 23,
                        ["portsDown"] = 1
                    }, "Sanitized interface fixture"),
                _ => throw new InvalidOperationException("Unexpected fixture command.")
            };
        }
    }

    private sealed class SplitRebootCollector : IDeviceCollector
    {
        private int _systemCalls;
        private int _logCalls;

        public Task<CollectedOutput> CollectAsync(
            SwitchOptions device,
            CommandDefinition command,
            CancellationToken cancellationToken)
        {
            var captured = DateTimeOffset.UtcNow;
            CollectedOutput output;
            if (command.Id == CommandIds.System)
            {
                var call = Interlocked.Increment(ref _systemCalls);
                output = new CollectedOutput(device.Id, command.Id, captured,
                    new JsonObject { ["uptimeSeconds"] = call == 1 ? 3600L : 30L },
                    "Sanitized system fixture");
            }
            else if (command.Id == CommandIds.LogRam)
            {
                var call = Interlocked.Increment(ref _logCalls);
                output = new CollectedOutput(device.Id, command.Id, captured,
                    new JsonObject
                    {
                        ["entries"] = new JsonArray(new JsonObject
                        {
                            ["id"] = call == 1 ? "before-reboot" : "after-reboot",
                            ["message"] = "Sanitized reboot fixture",
                            ["severity"] = "info"
                        })
                    }, "Sanitized log fixture");
            }
            else
            {
                throw new InvalidOperationException("Unexpected fixture command.");
            }
            return Task.FromResult(output);
        }
    }

    private sealed class ThrowingCollector : IDeviceCollector
    {
        public Task<CollectedOutput> CollectAsync(
            SwitchOptions device,
            CommandDefinition command,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("SENSITIVE fixture detail must never be persisted.");
    }

    private sealed class DuplicateBatchCollector : IDeviceCollector
    {
        public Task<CollectedOutput> CollectAsync(
            SwitchOptions device,
            CommandDefinition command,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Batch fixture only.");

        public Task<IReadOnlyList<CollectedOutput>> CollectBatchAsync(
            SwitchOptions device,
            IReadOnlyList<CommandDefinition> commands,
            CancellationToken cancellationToken)
        {
            var captured = DateTimeOffset.UtcNow;
            IReadOnlyList<CollectedOutput> outputs =
            [
                new(device.Id, commands[0].Id, captured, new JsonObject(), "Sanitized duplicate fixture"),
                new(device.Id, commands[0].Id, captured, new JsonObject(), "Sanitized duplicate fixture")
            ];
            return Task.FromResult(outputs);
        }
    }

    private sealed class CountingCollector : IDeviceCollector
    {
        public int Calls { get; private set; }

        public Task<CollectedOutput> CollectAsync(
            SwitchOptions device,
            CommandDefinition command,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new CollectedOutput(device.Id, command.Id, DateTimeOffset.UtcNow,
                new JsonObject(), "Sanitized counting fixture"));
        }
    }

    private sealed class ConcurrentBatchCollector(int expectedDevices) : IDeviceCollector
    {
        private readonly ConcurrentDictionary<string, int> _activeByDevice =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> _maximumSessionsByDevice =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly TaskCompletionSource _allDevicesCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _activeDevices;
        private int _completedDevices;
        private int _maximumConcurrentDevices;

        public Task AllDevicesCompleted => _allDevicesCompleted.Task;

        public int MaximumConcurrentDevices => Volatile.Read(ref _maximumConcurrentDevices);

        public IReadOnlyDictionary<string, int> MaximumSessionsByDevice => _maximumSessionsByDevice;

        public Task<CollectedOutput> CollectAsync(
            SwitchOptions device,
            CommandDefinition command,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The scheduler must use one batch per device.");

        public async Task<IReadOnlyList<CollectedOutput>> CollectBatchAsync(
            SwitchOptions device,
            IReadOnlyList<CommandDefinition> commands,
            CancellationToken cancellationToken)
        {
            var deviceSessions = _activeByDevice.AddOrUpdate(device.Id, 1, (_, current) => current + 1);
            _maximumSessionsByDevice.AddOrUpdate(device.Id,
                deviceSessions, (_, current) => Math.Max(current, deviceSessions));
            var active = Interlocked.Increment(ref _activeDevices);
            UpdateMaximum(ref _maximumConcurrentDevices, active);
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken);
                var captured = DateTimeOffset.UtcNow;
                return commands.Select(command => new CollectedOutput(device.Id, command.Id, captured,
                    command.Id switch
                    {
                        CommandIds.Version => new JsonObject
                        {
                            ["model"] = device.Model,
                            ["softwareVersion"] = "TEST"
                        },
                        CommandIds.System => new JsonObject { ["uptimeSeconds"] = 3600L, ["post"] = "PASS" },
                        CommandIds.LogRam => new JsonObject
                        {
                            ["entries"] = new JsonArray(new JsonObject
                            {
                                ["id"] = $"baseline-{device.Id}",
                                ["message"] = "Sanitized baseline",
                                ["severity"] = "info"
                            })
                        },
                        CommandIds.InterfaceStatus => new JsonObject
                        {
                            ["uplinkPort"] = device.UplinkPort,
                            ["uplinkAdminUp"] = true,
                            ["uplinkOperationalUp"] = true,
                            ["portsUp"] = 24,
                            ["portsDown"] = 0
                        },
                        _ => throw new InvalidOperationException("Unexpected command id.")
                    }, "Sanitized concurrency fixture")).ToArray();
            }
            finally
            {
                _activeByDevice.AddOrUpdate(device.Id, 0, (_, current) => Math.Max(0, current - 1));
                Interlocked.Decrement(ref _activeDevices);
                if (Interlocked.Increment(ref _completedDevices) == expectedDevices)
                {
                    _allDevicesCompleted.TrySetResult();
                }
            }
        }

        private static void UpdateMaximum(ref int target, int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref target);
                if (candidate <= current || Interlocked.CompareExchange(ref target, candidate, current) == current)
                {
                    return;
                }
            }
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

    private static DeviceProfileRegistry CreateProfileRegistry() => new(
    [
        Ies4224GpProfile.Create(),
        Ies4028XpProfile.Create(),
        Ies4226XpProfile.Create()
    ]);

    private static IReadOnlyDictionary<string, string?> MultiDeviceOverrides() =>
        new Dictionary<string, string?>
        {
            ["Agent:Switches:0:Id"] = "SW-01",
            ["Agent:Switches:0:Model"] = "IES4224GP",
            ["Agent:Switches:0:Host"] = "192.0.2.10",
            ["Agent:Switches:1:Id"] = "SW-02",
            ["Agent:Switches:1:Model"] = "IES4028XP",
            ["Agent:Switches:1:Host"] = "192.0.2.11",
            ["Agent:Switches:2:Id"] = "SW-03",
            ["Agent:Switches:2:Model"] = "IES4226XP",
            ["Agent:Switches:2:Host"] = "192.0.2.12"
        };
}
