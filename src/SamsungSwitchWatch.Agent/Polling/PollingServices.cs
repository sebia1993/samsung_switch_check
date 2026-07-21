using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.SignalR;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Persistence;
using SamsungSwitchWatch.Agent.Security;
using SamsungSwitchWatch.Core.Diagnostics;
using SamsungSwitchWatch.Core.Parsing;
using SamsungSwitchWatch.Core.Profiles;
using SamsungSwitchWatch.Core.Telnet;
using CoreAdministrativeState = SamsungSwitchWatch.Core.Models.AdministrativeState;
using CoreLinkState = SamsungSwitchWatch.Core.Models.LinkState;

namespace SamsungSwitchWatch.Agent.Polling;

public sealed record CommandDefinition(string Id, string Cli, TimeSpan Interval);

internal sealed record DetectionPlan(
    IReadOnlyList<NewEvent> NewEvents,
    IReadOnlyList<ConditionRecoveryRequest> Recoveries)
{
    public static DetectionPlan Empty { get; } = new([], []);
}

internal sealed record CollectorStatePlan(
    IReadOnlyList<DeviceSnapshot> Snapshots,
    IReadOnlyList<NewEvent> NewEvents,
    IReadOnlyList<ConditionRecoveryRequest> Recoveries);

public sealed record BatchCommandExecutionResult(
    string CommandId,
    bool Success,
    string? ErrorCode);

public static class CommandCatalog
{
    public const string CollectorHealthSnapshotId = "collector_health";
    public const string CollectorAuthCircuitSnapshotId = "collector_auth_circuit";
    public const string RebootCorrelationSnapshotId = "collector_reboot_correlation";

    public static string CollectorHealthSnapshotIdFor(string commandId) =>
        $"{CollectorHealthSnapshotId}:{commandId}";

    public static readonly IReadOnlyDictionary<string, CommandDefinition> Registered =
        new Dictionary<string, CommandDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["version"] = new("version", "show version", TimeSpan.FromHours(1)),
            ["system"] = new("system", "show system", TimeSpan.FromMinutes(1)),
            ["log_ram"] = new("log_ram", "show log ram", TimeSpan.FromMinutes(1)),
            [CommandIds.InterfaceStatus] = new(CommandIds.InterfaceStatus, "show interfaces status", TimeSpan.FromMinutes(1))
        };

    public static bool TryGet(string id, out CommandDefinition definition) => Registered.TryGetValue(id, out definition!);
}

public interface IDeviceCollector
{
    Task<CollectedOutput> CollectAsync(SwitchOptions device, CommandDefinition command, CancellationToken cancellationToken);

    async Task<IReadOnlyList<CollectedOutput>> CollectBatchAsync(
        SwitchOptions device,
        IReadOnlyList<CommandDefinition> commands,
        CancellationToken cancellationToken)
    {
        var outputs = new List<CollectedOutput>(commands.Count);
        foreach (var command in commands)
        {
            outputs.Add(await CollectAsync(device, command, cancellationToken));
        }
        return outputs;
    }
}

public sealed class MockDeviceCollector : IDeviceCollector
{
    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

    public Task<CollectedOutput> CollectAsync(SwitchOptions device, CommandDefinition command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var callKey = $"{device.Id}:{command.Id}";
        var call = _calls.AddOrUpdate(callKey, 1, (_, value) => value + 1);
        var now = DateTimeOffset.UtcNow;
        var structured = command.Id switch
        {
            "version" => new JsonObject
            {
                ["model"] = "IES4224GP",
                ["softwareVersion"] = "MOCK-1.0.0",
                ["mainPower"] = "normal",
                ["redundantPower"] = "not-present"
            },
            "system" => new JsonObject
            {
                ["uptimeSeconds"] = (long)(now - _started).TotalSeconds + call,
                ["post"] = "PASS",
                ["collector"] = "mock"
            },
            "log_ram" => BuildLogs(call, now),
            CommandIds.InterfaceStatus => BuildInterfaces(call, device.UplinkPort),
            _ => throw new AgentOperationException(AgentErrorCodes.CommandNotAllowed, "Command id is not registered.", 400)
        };

        var raw = $"MOCK FIXTURE - Agent only\r\nDevice: {device.Id}\r\nCommand: {command.Cli}\r\nCaptured: {now:O}\r\n";
        return Task.FromResult(new CollectedOutput(device.Id, command.Id, now, structured, raw));
    }

    private static JsonObject BuildLogs(int call, DateTimeOffset now)
    {
        var logs = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "baseline-1",
                ["occurredUtc"] = now.AddMinutes(-5).ToString("O"),
                ["severity"] = "info",
                ["message"] = "Mock collector baseline established."
            }
        };
        if (call >= 2)
        {
            logs.Add(new JsonObject
            {
                ["id"] = $"mock-log-{call / 2}",
                ["occurredUtc"] = now.ToString("O"),
                ["severity"] = "warning",
                ["message"] = "Simulated switch log entry."
            });
        }
        return new JsonObject { ["entries"] = logs };
    }

    private static JsonObject BuildInterfaces(int call, string uplinkPort)
    {
        var phase = (call - 1) % 4;
        var up = phase is 0 or 3;
        return new JsonObject
        {
            ["uplinkPort"] = uplinkPort,
            ["uplinkAdminUp"] = true,
            ["uplinkOperationalUp"] = up,
            ["portsUp"] = up ? 24 : 23,
            ["portsDown"] = up ? 0 : 1
        };
    }
}

public sealed class LiveCollectorNotConfigured : IDeviceCollector
{
    public Task<CollectedOutput> CollectAsync(SwitchOptions device, CommandDefinition command, CancellationToken cancellationToken) =>
        throw new AgentOperationException(AgentErrorCodes.ParserUnsupported,
            "Live Telnet collection is not enabled for this profile.", 503);
}

public sealed class CoreTelnetDeviceCollector(
    ITelnetClient telnet,
    DeviceProfileRegistry profiles,
    ICredentialVault credentials) : IDeviceCollector
{
    public async Task<CollectedOutput> CollectAsync(SwitchOptions device, CommandDefinition command, CancellationToken cancellationToken)
    {
        var outputs = await CollectBatchAsync(device, [command], cancellationToken);
        var output = outputs.Single();
        if (!string.Equals(output.CollectorStatus, "OK", StringComparison.OrdinalIgnoreCase))
        {
            throw new AgentOperationException(output.CollectorStatus,
                "The switch returned incomplete or unsupported read-only command output.", 503);
        }
        return output;
    }

    public async Task<IReadOnlyList<CollectedOutput>> CollectBatchAsync(
        SwitchOptions device,
        IReadOnlyList<CommandDefinition> commands,
        CancellationToken cancellationToken)
    {
        if (!profiles.TryGet(device.Model, out var profile))
        {
            var captured = DateTimeOffset.UtcNow;
            return commands.Select(command => new CollectedOutput(device.Id, command.Id, captured, [], string.Empty,
                AgentErrorCodes.ParserUnsupported)).ToArray();
        }

        var supported = commands.Where(command => profile.TryGetCommand(command.Id, out _)).ToArray();
        var unsupported = commands.Where(command => !profile.TryGetCommand(command.Id, out _))
            .Select(command => command.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (supported.Length == 0)
        {
            var captured = DateTimeOffset.UtcNow;
            return commands.Select(command => new CollectedOutput(device.Id, command.Id, captured, [], string.Empty,
                AgentErrorCodes.ParserUnsupported)).ToArray();
        }

        var credential = await credentials.GetAsync(device.CredentialId, cancellationToken)
            ?? throw new AgentOperationException(AgentErrorCodes.AuthFailed,
                "Switch credential is not configured on the Agent.", 503);
        try
        {
            var session = await telnet.ExecuteRegisteredAsync(
                new TelnetEndpoint(device.Host, device.Port),
                new TelnetCredentials(credential.Username, credential.Password),
                profile,
                supported.Select(command => command.Id).ToArray(),
                cancellationToken);
            var parsed = SamsungSnapshotParser.Parse(device.Id, session.Outputs, session.CompletedAt);
            var results = new List<CollectedOutput>(commands.Count);
            foreach (var command in commands)
            {
                if (unsupported.Contains(command.Id))
                {
                    results.Add(new CollectedOutput(device.Id, command.Id, session.CompletedAt, [], string.Empty,
                        AgentErrorCodes.ParserUnsupported));
                    continue;
                }
                var output = session.Outputs.Single(item =>
                    string.Equals(item.CommandId, command.Id, StringComparison.OrdinalIgnoreCase));
                try
                {
                    ValidateComplete(command.Id, device.UplinkPort, parsed);
                    var structured = ConvertSnapshot(command.Id, device.UplinkPort, parsed);
                    results.Add(new CollectedOutput(device.Id, command.Id, output.CollectedAt, structured, output.RawOutput));
                }
                catch (AgentOperationException ex)
                {
                    results.Add(new CollectedOutput(device.Id, command.Id, output.CollectedAt, [], output.RawOutput, ex.Code));
                }
            }
            return results;
        }
        catch (SwitchWatchException ex)
        {
            throw new AgentOperationException(NormalizeCode(ex.Error.Code), ex.Error.Message, ex.Error.IsRetryable ? 503 : 400);
        }
    }

    private static void ValidateComplete(
        string commandId,
        string uplinkPort,
        SamsungSwitchWatch.Core.Models.DeviceSnapshot snapshot)
    {
        var complete = commandId switch
        {
            CommandIds.Version => snapshot.Version is not null,
            CommandIds.System => snapshot.System is not null,
            CommandIds.LogRam => snapshot.Logs is not null,
            CommandIds.InterfaceStatus => snapshot.Interfaces?.Interfaces.Values.Any(item =>
                string.Equals(item.PortId, uplinkPort, StringComparison.OrdinalIgnoreCase)) == true,
            _ => false
        };
        if (!complete)
        {
            var issueCode = snapshot.Issues.FirstOrDefault(issue =>
                string.Equals(issue.CommandId, commandId, StringComparison.OrdinalIgnoreCase))?.Error.Code;
            var code = string.IsNullOrWhiteSpace(issueCode)
                ? AgentErrorCodes.IncompleteOutput
                : NormalizeCode(issueCode);
            if (commandId == CommandIds.InterfaceStatus && snapshot.Interfaces is not null)
            {
                code = AgentErrorCodes.IncompleteOutput;
            }
            throw new AgentOperationException(code,
                "The switch returned incomplete or unsupported read-only command output.", 503);
        }
    }

    private static JsonObject ConvertSnapshot(string commandId, string uplinkPort, SamsungSwitchWatch.Core.Models.DeviceSnapshot snapshot)
    {
        if (commandId == CommandIds.Version && snapshot.Version is { } version)
        {
            return new JsonObject
            {
                ["model"] = version.Model,
                ["softwareVersion"] = version.SoftwareVersion,
                ["hardwareVersion"] = version.HardwareVersion,
                ["mainPower"] = version.MainPowerStatus,
                ["redundantPower"] = version.RedundantPowerStatus
            };
        }

        if (commandId == CommandIds.System && snapshot.System is { } system)
        {
            var checks = new JsonObject();
            foreach (var check in system.PostChecks)
            {
                checks[check.Key] = check.Value;
            }
            return new JsonObject
            {
                ["uptimeSeconds"] = system.Uptime.HasValue ? (long)system.Uptime.Value.TotalSeconds : null,
                ["postChecks"] = checks
            };
        }

        if (commandId == CommandIds.LogRam && snapshot.Logs is { } logs)
        {
            var entries = new JsonArray();
            foreach (var entry in logs.Entries)
            {
                entries.Add(new JsonObject
                {
                    ["id"] = entry.Identity,
                    ["sequence"] = entry.SequenceNumber,
                    ["deviceTimestamp"] = entry.DeviceTimestamp?.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["message"] = DiagnosticRedactor.Redact(entry.Message),
                    ["level"] = entry.Level,
                    ["module"] = entry.Module,
                    ["function"] = entry.Function,
                    ["eventNumber"] = entry.EventNumber
                });
            }
            return new JsonObject { ["entries"] = entries };
        }

        if (commandId == CommandIds.InterfaceStatus && snapshot.Interfaces is { } interfaces)
        {
            var uplink = interfaces.Interfaces.Values.FirstOrDefault(item =>
                string.Equals(item.PortId, uplinkPort, StringComparison.OrdinalIgnoreCase));
            return new JsonObject
            {
                ["uplinkPort"] = uplinkPort,
                ["uplinkAdminUp"] = uplink?.AdministrativeState == CoreAdministrativeState.Enabled,
                ["uplinkOperationalUp"] = uplink?.OperationalState == CoreLinkState.Up,
                ["portsUp"] = interfaces.Interfaces.Values.Count(item => item.OperationalState == CoreLinkState.Up),
                ["portsDown"] = interfaces.Interfaces.Values.Count(item => item.OperationalState == CoreLinkState.Down),
                ["portCount"] = interfaces.Interfaces.Count
            };
        }

        var issue = snapshot.Issues.FirstOrDefault(item =>
            string.Equals(item.CommandId, commandId, StringComparison.OrdinalIgnoreCase));
        return new JsonObject
        {
            ["collectorStatus"] = issue?.Error.Code ?? AgentErrorCodes.ParserUnsupported,
            ["stage"] = issue?.Error.Stage ?? "parse"
        };
    }

    private static string NormalizeCode(string code) => code switch
    {
        AgentErrorCodes.TcpTimeout or
        AgentErrorCodes.TelnetNegotiationFailed or
        AgentErrorCodes.LoginPromptNotFound or
        AgentErrorCodes.AuthFailed or
        AgentErrorCodes.CommandTimeout or
        AgentErrorCodes.PromptParseFailed or
         AgentErrorCodes.OutputLimitExceeded or
         AgentErrorCodes.IncompleteOutput or
         AgentErrorCodes.ParserUnsupported => code,
        _ => AgentErrorCodes.PromptParseFailed
    };
}

public sealed class AgentEventsHub : Hub;

public sealed class EventPublisher(
    SqliteAgentStore store,
    IHubContext<AgentEventsHub> hub,
    ILogger<EventPublisher> logger)
{
    public async Task<StructuredEvent> PublishAsync(NewEvent item, CancellationToken cancellationToken)
    {
        var change = await store.InsertEventChangeAsync(item, cancellationToken);
        await BroadcastBestEffortAsync(change, "eventReceived", cancellationToken);
        return change.Event;
    }

    public async Task<StructuredEvent?> AcknowledgeAsync(string id, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var change = await store.AcknowledgeEventChangeAsync(id, at, cancellationToken);
        if (change is not null)
        {
            await BroadcastBestEffortAsync(change, "eventUpdated", cancellationToken);
            return change.Event;
        }
        return await store.GetEventByIdAsync(id, cancellationToken);
    }

    public async Task<StructuredEvent> RecoverConditionAndPublishAsync(
        string deviceId,
        string conditionKey,
        DateTimeOffset at,
        NewEvent recoveryEvent,
        CancellationToken cancellationToken)
    {
        var changes = await store.RecoverConditionAndInsertEventAsync(
            deviceId, conditionKey, at, recoveryEvent, cancellationToken);
        foreach (var change in changes)
        {
            var legacyMethod = change.ChangeKind == EventChangeKind.Created ? "eventReceived" : "eventUpdated";
            await BroadcastBestEffortAsync(change, legacyMethod, cancellationToken);
        }
        return changes[^1].Event;
    }

    public async Task BroadcastCommittedAsync(
        IReadOnlyList<EventChange> changes,
        CancellationToken cancellationToken)
    {
        foreach (var change in changes)
        {
            var legacyMethod = change.ChangeKind == EventChangeKind.Created ? "eventReceived" : "eventUpdated";
            await BroadcastBestEffortAsync(change, legacyMethod, cancellationToken);
        }
    }

    private async Task BroadcastBestEffortAsync(EventChange change, string legacyMethod, CancellationToken cancellationToken)
    {
        try
        {
            await hub.Clients.All.SendAsync("eventChanged", change, cancellationToken);
            await hub.Clients.All.SendAsync(legacyMethod, change.Event, cancellationToken);
        }
        catch (Exception)
        {
            logger.LogWarning("Live event delivery failed for change {ChangeSequence}; durable catch-up remains available.",
                change.ChangeSequence);
        }
    }
}

public sealed class CommandExecutionService(
    AgentOptions options,
    IDeviceCollector collector,
    SqliteAgentStore store,
    EventPublisher publisher,
    ILogger<CommandExecutionService> logger)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceGates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<CommandExecutionResult> ExecuteAsync(
        string deviceId,
        string commandId,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var device = options.Switches.SingleOrDefault(item => string.Equals(item.Id, deviceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new AgentOperationException(AgentErrorCodes.DeviceNotFound, "Device was not found.", 404);
        if (!CommandCatalog.TryGet(commandId, out var command))
        {
            await store.InsertAuditAsync(new AuditEntry(DateTimeOffset.UtcNow, "command", actor, deviceId,
                "denied", $"Unregistered command id: {Sanitize(commandId)}"), cancellationToken);
            throw new AgentOperationException(AgentErrorCodes.CommandNotAllowed, "Command id is not registered.", 400);
        }

        var gate = _deviceGates.GetOrAdd(device.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureStorageWritable();
            var previous = await store.GetSnapshotAsync(device.Id, command.Id, cancellationToken);
            var healthSnapshotId = CommandCatalog.CollectorHealthSnapshotIdFor(command.Id);
            var previousHealth = await store.GetSnapshotAsync(device.Id, healthSnapshotId, cancellationToken);
            var authCircuit = await store.GetSnapshotAsync(
                device.Id, CommandCatalog.CollectorAuthCircuitSnapshotId, cancellationToken);
            var blockedCode = AuthCircuitErrorCode(device, authCircuit);
            if (blockedCode is not null)
            {
                await store.InsertAuditAsync(new AuditEntry(DateTimeOffset.UtcNow, "command", actor, device.Id,
                    "blocked", $"Error code: {blockedCode}"), cancellationToken);
                throw new AgentOperationException(blockedCode,
                    "Switch authentication is blocked until the stored credential changes.", 503);
            }
            CollectedOutput output;
            try
            {
                output = await collector.CollectAsync(device, command, cancellationToken);
                if (!string.Equals(output.CollectorStatus, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    throw new AgentOperationException(
                        NormalizeCollectorErrorCode(output.CollectorStatus),
                        "The switch returned incomplete or unsupported read-only command output.",
                        503);
                }
            }
            catch (AgentOperationException ex)
            {
                var safeCode = NormalizeCollectorErrorCode(ex.Code);
                var statePlan = await BuildCollectorFailurePlanAsync(
                    device, command.Id, previousHealth, authCircuit, safeCode, cancellationToken);
                logger.LogWarning("Collection failed for {DeviceId}/{CommandId} with {ErrorCode}.", device.Id, command.Id, safeCode);
                var changes = await store.CommitCollectorStateAsync(statePlan.Snapshots,
                    new AuditEntry(DateTimeOffset.UtcNow, "command", actor, device.Id,
                        "failed", $"Error code: {safeCode}"),
                    statePlan.NewEvents, statePlan.Recoveries, cancellationToken);
                await publisher.BroadcastCommittedAsync(changes, cancellationToken);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                const string safeCode = AgentErrorCodes.PromptParseFailed;
                logger.LogWarning("Unexpected collector failure for {DeviceId}/{CommandId}; type {ExceptionType}.",
                    device.Id, command.Id, ex.GetType().Name);
                var statePlan = await BuildCollectorFailurePlanAsync(
                    device, command.Id, previousHealth, authCircuit, safeCode, cancellationToken);
                var changes = await store.CommitCollectorStateAsync(statePlan.Snapshots,
                    new AuditEntry(DateTimeOffset.UtcNow, "command", actor, device.Id,
                        "failed", $"Error code: {safeCode}"),
                    statePlan.NewEvents, statePlan.Recoveries, cancellationToken);
                await publisher.BroadcastCommittedAsync(changes, cancellationToken);
                throw new AgentOperationException(safeCode,
                    "The collector could not process the read-only command output.", 503);
            }

            try
            {
                return await PersistSuccessfulOutputAsync(device, command, actor, previous, previousHealth,
                    authCircuit, output, suppressLogBufferReset: false, cancellationToken);
            }
            catch (Exception ex) when (ex is not AgentOperationException and not OperationCanceledException)
            {
                const string safeCode = AgentErrorCodes.PromptParseFailed;
                logger.LogWarning("Unexpected collection processing failure for {DeviceId}/{CommandId}; type {ExceptionType}.",
                    device.Id, command.Id, ex.GetType().Name);
                var statePlan = await BuildCollectorFailurePlanAsync(
                    device, command.Id, previousHealth, authCircuit, safeCode, cancellationToken);
                var changes = await store.CommitCollectorStateAsync(statePlan.Snapshots,
                    new AuditEntry(DateTimeOffset.UtcNow, "command", actor, device.Id,
                        "failed", $"Error code: {safeCode}"),
                    statePlan.NewEvents, statePlan.Recoveries, cancellationToken);
                await publisher.BroadcastCommittedAsync(changes, cancellationToken);
                throw new AgentOperationException(safeCode,
                    "The collector could not process the read-only command output.", 503);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<BatchCommandExecutionResult>> ExecuteBatchAsync(
        string deviceId,
        IReadOnlyList<string> commandIds,
        string actor,
        CancellationToken cancellationToken = default)
    {
        var device = options.Switches.SingleOrDefault(item => string.Equals(item.Id, deviceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new AgentOperationException(AgentErrorCodes.DeviceNotFound, "Device was not found.", 404);
        var commands = commandIds.Distinct(StringComparer.OrdinalIgnoreCase).Select(id =>
        {
            if (!CommandCatalog.TryGet(id, out var definition))
            {
                throw new AgentOperationException(AgentErrorCodes.CommandNotAllowed, "Command id is not registered.", 400);
            }
            return definition;
        }).ToArray();
        if (commands.Length == 0)
        {
            return [];
        }

        var gate = _deviceGates.GetOrAdd(device.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            EnsureStorageWritable();
            var authCircuit = await store.GetSnapshotAsync(
                device.Id, CommandCatalog.CollectorAuthCircuitSnapshotId, cancellationToken);
            var blockedCode = AuthCircuitErrorCode(device, authCircuit);
            if (blockedCode is not null)
            {
                await store.InsertAuditAsync(new AuditEntry(DateTimeOffset.UtcNow, "batch-command", actor, device.Id,
                    "blocked", $"Commands skipped: {commands.Length}; error code: {blockedCode}"), cancellationToken);
                return commands.Select(command => new BatchCommandExecutionResult(
                    command.Id, false, blockedCode)).ToArray();
            }

            var previous = new Dictionary<string, DeviceSnapshot?>(StringComparer.OrdinalIgnoreCase);
            var previousHealth = new Dictionary<string, DeviceSnapshot?>(StringComparer.OrdinalIgnoreCase);
            foreach (var command in commands)
            {
                previous[command.Id] = await store.GetSnapshotAsync(device.Id, command.Id, cancellationToken);
                previousHealth[command.Id] = await store.GetSnapshotAsync(device.Id,
                    CommandCatalog.CollectorHealthSnapshotIdFor(command.Id), cancellationToken);
            }

            IReadOnlyList<CollectedOutput> outputs;
            try
            {
                outputs = await collector.CollectBatchAsync(device, commands, cancellationToken);
            }
            catch (AgentOperationException ex)
            {
                var safeCode = NormalizeCollectorErrorCode(ex.Code);
                var stateSnapshots = new List<DeviceSnapshot>();
                var stateEvents = new List<NewEvent>();
                var stateRecoveries = new List<ConditionRecoveryRequest>();
                foreach (var command in commands)
                {
                    var plan = await BuildCollectorFailurePlanAsync(device, command.Id,
                        previousHealth[command.Id], authCircuit, safeCode, cancellationToken);
                    stateSnapshots.AddRange(plan.Snapshots.Where(snapshot =>
                        !string.Equals(snapshot.CommandId, CommandCatalog.CollectorHealthSnapshotId, StringComparison.OrdinalIgnoreCase)));
                    stateEvents.AddRange(plan.NewEvents);
                    stateRecoveries.AddRange(plan.Recoveries);
                    if (IsAuthenticationCircuitCode(safeCode) && authCircuit is null)
                    {
                        authCircuit = plan.Snapshots.FirstOrDefault(snapshot =>
                            string.Equals(snapshot.CommandId, CommandCatalog.CollectorAuthCircuitSnapshotId,
                                StringComparison.OrdinalIgnoreCase));
                    }
                }
                var combined = await CompleteCollectorStatePlanAsync(device.Id, stateSnapshots,
                    stateEvents, stateRecoveries, DateTimeOffset.UtcNow, cancellationToken);
                var changes = await store.CommitCollectorStateAsync(combined.Snapshots,
                    new AuditEntry(DateTimeOffset.UtcNow, "batch-command", actor, device.Id,
                        "failed", $"Commands failed: {commands.Length}; error code: {safeCode}"),
                    combined.NewEvents, combined.Recoveries, cancellationToken);
                await publisher.BroadcastCommittedAsync(changes, cancellationToken);
                return commands.Select(command => new BatchCommandExecutionResult(command.Id, false, safeCode)).ToArray();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Unexpected batch collector failure for {DeviceId}; type {ExceptionType}.",
                    device.Id, ex.GetType().Name);
                return await PersistBatchFailureAsync(device, commands, previousHealth, authCircuit, actor,
                    AgentErrorCodes.PromptParseFailed, cancellationToken);
            }

            if (!HasCompleteUniqueBatch(outputs, commands, device.Id))
            {
                logger.LogWarning("Collector returned an invalid output set for {DeviceId}; commands {CommandCount}.",
                    device.Id, commands.Length);
                return await PersistBatchFailureAsync(device, commands, previousHealth, authCircuit, actor,
                    AgentErrorCodes.IncompleteOutput, cancellationToken);
            }

            var byCommand = outputs.ToDictionary(output => output.CommandId, StringComparer.OrdinalIgnoreCase);
            var rebootDetectedInBatch = byCommand.TryGetValue("system", out var currentSystem) &&
                                        string.Equals(currentSystem.CollectorStatus, "OK", StringComparison.OrdinalIgnoreCase) &&
                                        previous.TryGetValue("system", out var previousSystem) &&
                                        previousSystem is not null && HasRestarted(previousSystem.Data, currentSystem);
            var results = new List<BatchCommandExecutionResult>(commands.Length);
            foreach (var command in commands)
            {
                if (!byCommand.TryGetValue(command.Id, out var output) ||
                    !string.Equals(output.CollectorStatus, "OK", StringComparison.OrdinalIgnoreCase))
                {
                    var code = NormalizeCollectorErrorCode(output?.CollectorStatus ?? AgentErrorCodes.IncompleteOutput);
                    var plan = await BuildCollectorFailurePlanAsync(device, command.Id,
                        previousHealth[command.Id], authCircuit, code, cancellationToken);
                    var changes = await store.CommitCollectorStateAsync(plan.Snapshots,
                        new AuditEntry(DateTimeOffset.UtcNow, "command", actor, device.Id,
                            "failed", $"Error code: {code}"),
                        plan.NewEvents, plan.Recoveries, cancellationToken);
                    await publisher.BroadcastCommittedAsync(changes, cancellationToken);
                    results.Add(new BatchCommandExecutionResult(command.Id, false, code));
                    continue;
                }

                try
                {
                    await PersistSuccessfulOutputAsync(device, command, actor, previous[command.Id],
                        previousHealth[command.Id], authCircuit, output,
                        suppressLogBufferReset: rebootDetectedInBatch && command.Id == "log_ram", cancellationToken);
                }
                catch (Exception ex) when (ex is not AgentOperationException and not OperationCanceledException)
                {
                    const string safeCode = AgentErrorCodes.PromptParseFailed;
                    logger.LogWarning(
                        "Unexpected collection processing failure for {DeviceId}/{CommandId}; type {ExceptionType}.",
                        device.Id, command.Id, ex.GetType().Name);
                    var plan = await BuildCollectorFailurePlanAsync(device, command.Id,
                        previousHealth[command.Id], authCircuit, safeCode, cancellationToken);
                    var changes = await store.CommitCollectorStateAsync(plan.Snapshots,
                        new AuditEntry(DateTimeOffset.UtcNow, "command", actor, device.Id,
                            "failed", $"Error code: {safeCode}"),
                        plan.NewEvents, plan.Recoveries, cancellationToken);
                    await publisher.BroadcastCommittedAsync(changes, cancellationToken);
                    results.Add(new BatchCommandExecutionResult(command.Id, false, safeCode));
                    continue;
                }
                if (authCircuit?.Data["blocked"]?.GetValue<bool>() == true)
                {
                    authCircuit = null;
                }
                results.Add(new BatchCommandExecutionResult(command.Id, true, null));
            }
            return results;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool HasCompleteUniqueBatch(
        IReadOnlyList<CollectedOutput> outputs,
        IReadOnlyList<CommandDefinition> commands,
        string deviceId)
    {
        if (outputs.Count != commands.Count)
        {
            return false;
        }

        var expected = commands.Select(command => command.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var output in outputs)
        {
            if (!string.Equals(output.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) ||
                !expected.Remove(output.CommandId))
            {
                return false;
            }
        }
        return expected.Count == 0;
    }

    private void EnsureStorageWritable()
    {
        if (!store.WritesAllowed)
        {
            throw new AgentOperationException(AgentErrorCodes.StorageWriteFailed,
                "Agent storage writes are paused until integrity is restored.", 503);
        }
    }

    private async Task<IReadOnlyList<BatchCommandExecutionResult>> PersistBatchFailureAsync(
        SwitchOptions device,
        IReadOnlyList<CommandDefinition> commands,
        IReadOnlyDictionary<string, DeviceSnapshot?> previousHealth,
        DeviceSnapshot? authCircuit,
        string actor,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var safeCode = NormalizeCollectorErrorCode(errorCode);
        var snapshots = new List<DeviceSnapshot>();
        var events = new List<NewEvent>();
        var recoveries = new List<ConditionRecoveryRequest>();
        foreach (var command in commands)
        {
            var plan = await BuildCollectorFailurePlanAsync(device, command.Id,
                previousHealth[command.Id], authCircuit, safeCode, cancellationToken);
            snapshots.AddRange(plan.Snapshots.Where(snapshot =>
                !string.Equals(snapshot.CommandId, CommandCatalog.CollectorHealthSnapshotId,
                    StringComparison.OrdinalIgnoreCase)));
            events.AddRange(plan.NewEvents);
            recoveries.AddRange(plan.Recoveries);
            if (IsAuthenticationCircuitCode(safeCode) && authCircuit is null)
            {
                authCircuit = plan.Snapshots.FirstOrDefault(snapshot =>
                    string.Equals(snapshot.CommandId, CommandCatalog.CollectorAuthCircuitSnapshotId,
                        StringComparison.OrdinalIgnoreCase));
            }
        }

        var combined = await CompleteCollectorStatePlanAsync(device.Id, snapshots, events, recoveries,
            DateTimeOffset.UtcNow, cancellationToken);
        var changes = await store.CommitCollectorStateAsync(combined.Snapshots,
            new AuditEntry(DateTimeOffset.UtcNow, "batch-command", actor, device.Id,
                "failed", $"Commands failed: {commands.Count}; error code: {safeCode}"),
            combined.NewEvents, combined.Recoveries, cancellationToken);
        await publisher.BroadcastCommittedAsync(changes, cancellationToken);
        return commands.Select(command => new BatchCommandExecutionResult(command.Id, false, safeCode)).ToArray();
    }

    private async Task<CommandExecutionResult> PersistSuccessfulOutputAsync(
        SwitchOptions device,
        CommandDefinition command,
        string actor,
        DeviceSnapshot? previous,
        DeviceSnapshot? previousHealth,
        DeviceSnapshot? authCircuit,
        CollectedOutput output,
        bool suppressLogBufferReset,
        CancellationToken cancellationToken)
    {
        var statePlan = await BuildCollectorSuccessPlanAsync(device, command.Id, previousHealth, authCircuit,
            output.CapturedUtc, cancellationToken);
        var stateSnapshots = statePlan.Snapshots.ToList();
        if (string.Equals(command.Id, CommandIds.System, StringComparison.OrdinalIgnoreCase) &&
            previous is not null && HasRestarted(previous.Data, output))
        {
            stateSnapshots.Add(new DeviceSnapshot(device.Id, CommandCatalog.RebootCorrelationSnapshotId,
                output.CapturedUtc, new JsonObject
                {
                    ["restartDetectedUtc"] = output.CapturedUtc.ToString("O"),
                    ["pendingLogBaseline"] = true
                }));
        }

        if (string.Equals(command.Id, CommandIds.LogRam, StringComparison.OrdinalIgnoreCase))
        {
            var correlation = await store.GetSnapshotAsync(device.Id,
                CommandCatalog.RebootCorrelationSnapshotId, cancellationToken);
            var pending = correlation?.Data["pendingLogBaseline"]?.GetValue<bool>() == true;
            var withinWindow = pending && output.CapturedUtc - correlation!.CapturedUtc <= TimeSpan.FromMinutes(10);
            suppressLogBufferReset |= withinWindow && WouldResetLogBaseline(previous, output);
            if (pending)
            {
                stateSnapshots.Add(new DeviceSnapshot(device.Id, CommandCatalog.RebootCorrelationSnapshotId,
                    output.CapturedUtc, new JsonObject
                    {
                        ["restartDetectedUtc"] = correlation!.Data["restartDetectedUtc"]?.DeepClone(),
                        ["pendingLogBaseline"] = false,
                        ["consumedUtc"] = output.CapturedUtc.ToString("O")
                    }));
            }
        }

        var detection = DetectChanges(device, command.Id, previous, output, suppressLogBufferReset);
        var committedChanges = await store.CommitSuccessfulCollectionAsync(output,
            stateSnapshots,
            new AuditEntry(DateTimeOffset.UtcNow, "command", actor, device.Id,
                "success", $"Registered command id: {command.Id}"),
            statePlan.NewEvents.Concat(detection.NewEvents).ToArray(),
            statePlan.Recoveries.Concat(detection.Recoveries).ToArray(), cancellationToken);
        await publisher.BroadcastCommittedAsync(committedChanges, cancellationToken);
        return new CommandExecutionResult(device.Id, command.Id, output.CapturedUtc, output.CollectorStatus,
            output.Structured, detection.NewEvents.Count + detection.Recoveries.Count);
    }

    private async Task<CollectorStatePlan> BuildCollectorFailurePlanAsync(
        SwitchOptions device,
        string commandId,
        DeviceSnapshot? previousHealth,
        DeviceSnapshot? authCircuit,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var attempted = DateTimeOffset.UtcNow;
        var snapshots = new List<DeviceSnapshot>();
        var events = new List<NewEvent>();
        var previousFailures = previousHealth?.Data["consecutiveFailures"]?.GetValue<int>() ?? 0;
        var previousState = previousHealth?.Data["state"]?.GetValue<string>();
        var isUnsupported = string.Equals(errorCode, AgentErrorCodes.ParserUnsupported, StringComparison.Ordinal);
        var consecutiveFailures = isUnsupported
            ? string.Equals(previousState, "Unsupported", StringComparison.Ordinal) ? Math.Max(1, previousFailures) : 1
            : previousFailures + 1;
        var isAuthenticationFailure = IsAuthenticationCircuitCode(errorCode);
        var state = isAuthenticationFailure
            ? "AuthBlocked"
            : isUnsupported
                ? "Unsupported"
                : consecutiveFailures >= 3 ? "Failed" : "Degraded";
        snapshots.Add(new DeviceSnapshot(device.Id,
            CommandCatalog.CollectorHealthSnapshotIdFor(commandId), attempted,
            new JsonObject
            {
                ["errorCode"] = errorCode,
                ["lastAttemptUtc"] = attempted.ToString("O"),
                ["lastSuccessUtc"] = previousHealth?.Data["lastSuccessUtc"]?.DeepClone(),
                ["consecutiveFailures"] = consecutiveFailures,
                ["state"] = state
            }));

        if (isUnsupported)
        {
            if (!string.Equals(previousState, "Unsupported", StringComparison.Ordinal))
            {
                events.Add(new NewEvent(device.Id, EventSeverity.Warning, "collector-command-unsupported",
                    "Unsupported collection command",
                    "The switch does not support this read-only command or output profile. The command will be retried hourly.",
                    EventState.New, CollectorCondition(device.Id, commandId), new Dictionary<string, string>
                    {
                        ["commandId"] = commandId,
                        ["errorCode"] = errorCode
                    }, IsActiveCondition: true));
            }
        }
        else if (isAuthenticationFailure)
        {
            var wasBlocked = authCircuit?.Data["blocked"]?.GetValue<bool>() == true;
            snapshots.Add(new DeviceSnapshot(device.Id,
                CommandCatalog.CollectorAuthCircuitSnapshotId, attempted,
                new JsonObject
                {
                    ["blocked"] = true,
                    ["errorCode"] = errorCode,
                    ["lastAttemptUtc"] = attempted.ToString("O"),
                    ["credentialVersion"] = CredentialVersion(device)
                }));
            if (!wasBlocked)
            {
                events.Add(new NewEvent(device.Id, EventSeverity.Critical, "collector-auth-blocked",
                    "스위치 인증 차단", "인증 실패로 자동 수집을 중단했습니다. 저장된 자격 증명을 갱신하십시오.", EventState.New,
                    AuthCondition(device.Id), new Dictionary<string, string> { ["errorCode"] = errorCode },
                    IsActiveCondition: true));
            }
        }
        else if (consecutiveFailures == 3)
        {
            events.Add(new NewEvent(device.Id, EventSeverity.Critical, "collector-failed",
                "스위치 수집 실패", $"수집에 실패했습니다. 오류 코드: {errorCode}", EventState.New,
                CollectorCondition(device.Id, commandId), new Dictionary<string, string>
                {
                    ["commandId"] = commandId,
                    ["errorCode"] = errorCode
                },
                IsActiveCondition: true));
        }
        return await CompleteCollectorStatePlanAsync(device.Id, snapshots, events, [], attempted, cancellationToken);
    }

    private async Task<CollectorStatePlan> BuildCollectorSuccessPlanAsync(
        SwitchOptions device,
        string commandId,
        DeviceSnapshot? previousHealth,
        DeviceSnapshot? authCircuit,
        DateTimeOffset attempted,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<DeviceSnapshot>();
        var events = new List<NewEvent>();
        var recoveries = new List<ConditionRecoveryRequest>();
        var previousFailures = previousHealth?.Data["consecutiveFailures"]?.GetValue<int>() ?? 0;
        var previousCode = previousHealth?.Data["errorCode"]?.GetValue<string>();
        var previousState = previousHealth?.Data["state"]?.GetValue<string>();
        snapshots.Add(new DeviceSnapshot(device.Id,
            CommandCatalog.CollectorHealthSnapshotIdFor(commandId), attempted,
            new JsonObject
            {
                ["errorCode"] = null,
                ["lastAttemptUtc"] = attempted.ToString("O"),
                ["lastSuccessUtc"] = attempted.ToString("O"),
                ["consecutiveFailures"] = 0,
                ["state"] = "Healthy"
            }));

        if (string.Equals(previousState, "Unsupported", StringComparison.Ordinal))
        {
            var recoveryEvent = new NewEvent(device.Id, EventSeverity.Recovery, "collector-command-supported",
                "Collection command supported",
                "The read-only command is producing supported output again.", EventState.Recovered,
                CollectorCondition(device.Id, commandId), new Dictionary<string, string>
                {
                    ["commandId"] = commandId,
                    ["previousErrorCode"] = AgentErrorCodes.ParserUnsupported
                });
            recoveries.Add(new ConditionRecoveryRequest(device.Id,
                CollectorCondition(device.Id, commandId), attempted, recoveryEvent));
        }
        else if (previousFailures >= 3 &&
                 (string.IsNullOrWhiteSpace(previousCode) || !IsAuthenticationCircuitCode(previousCode)))
        {
            var details = string.IsNullOrWhiteSpace(previousCode)
                ? null
                : new Dictionary<string, string>
                {
                    ["commandId"] = commandId,
                    ["previousErrorCode"] = NormalizeCollectorErrorCode(previousCode)
                };
            var recoveryEvent = new NewEvent(device.Id, EventSeverity.Recovery, "collector-recovered",
                "스위치 수집 복구", "Agent가 스위치 상태를 다시 정상적으로 수집합니다.", EventState.Recovered,
                CollectorCondition(device.Id, commandId), details);
            recoveries.Add(new ConditionRecoveryRequest(device.Id,
                CollectorCondition(device.Id, commandId), attempted, recoveryEvent));
        }

        if (authCircuit?.Data["blocked"]?.GetValue<bool>() == true)
        {
            snapshots.Add(new DeviceSnapshot(device.Id,
                CommandCatalog.CollectorAuthCircuitSnapshotId, attempted,
                new JsonObject
                {
                    ["blocked"] = false,
                    ["errorCode"] = null,
                    ["lastAttemptUtc"] = attempted.ToString("O"),
                    ["lastSuccessUtc"] = attempted.ToString("O"),
                    ["credentialVersion"] = CredentialVersion(device)
                }));
            var authRecovery = new NewEvent(device.Id, EventSeverity.Recovery, "collector-auth-recovered",
                    "스위치 인증 복구", "갱신된 자격 증명으로 스위치 읽기 전용 수집을 재개했습니다.", EventState.Recovered,
                    AuthCondition(device.Id));
            recoveries.Add(new ConditionRecoveryRequest(device.Id, AuthCondition(device.Id), attempted, authRecovery));
        }
        return await CompleteCollectorStatePlanAsync(device.Id, snapshots, events, recoveries, attempted, cancellationToken);
    }

    private string? AuthCircuitErrorCode(SwitchOptions device, DeviceSnapshot? circuit)
    {
        if (circuit?.Data["blocked"]?.GetValue<bool>() != true ||
            !string.Equals(circuit.Data["credentialVersion"]?.GetValue<string>(), CredentialVersion(device), StringComparison.Ordinal))
        {
            return null;
        }
        return NormalizeCollectorErrorCode(circuit.Data["errorCode"]?.GetValue<string>() ?? AgentErrorCodes.AuthFailed);
    }

    private static bool IsAuthenticationCircuitCode(string code) => code is
        AgentErrorCodes.AuthFailed or AgentErrorCodes.CredentialCorrupt or AgentErrorCodes.CredentialUnavailable;

    private string CredentialVersion(SwitchOptions device)
    {
        var path = Path.Combine(options.DataDirectory, "credentials", $"{device.CredentialId}.bin");
        if (!File.Exists(path))
        {
            return "missing";
        }
        var info = new FileInfo(path);
        return $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
    }

    private async Task<CollectorStatePlan> CompleteCollectorStatePlanAsync(
        string deviceId,
        IReadOnlyList<DeviceSnapshot> plannedSnapshots,
        IReadOnlyList<NewEvent> events,
        IReadOnlyList<ConditionRecoveryRequest> recoveries,
        DateTimeOffset captured,
        CancellationToken cancellationToken)
    {
        var prefix = CommandCatalog.CollectorHealthSnapshotId + ":";
        var existing = (await store.GetAllSnapshotsAsync(cancellationToken))
            .Where(snapshot => string.Equals(snapshot.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) &&
                               snapshot.CommandId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(snapshot => !plannedSnapshots.Any(planned =>
                string.Equals(planned.CommandId, snapshot.CommandId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        var commandHealth = existing.Concat(plannedSnapshots.Where(snapshot =>
                snapshot.CommandId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        if (commandHealth.Length == 0)
        {
            return new CollectorStatePlan(plannedSnapshots, events, recoveries);
        }

        static int Rank(string? state) => state switch
        {
            "AuthBlocked" => 3,
            "Failed" => 2,
            "Degraded" or "Unsupported" => 1,
            _ => 0
        };
        var worst = commandHealth
            .OrderByDescending(snapshot => Rank(snapshot.Data["state"]?.GetValue<string>()))
            .ThenByDescending(snapshot => snapshot.CapturedUtc)
            .First();
        var lastSuccess = commandHealth
            .Select(snapshot => snapshot.Data["lastSuccessUtc"]?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .OrderByDescending(value => value, StringComparer.Ordinal)
            .FirstOrDefault();
        var snapshots = plannedSnapshots.ToList();
        snapshots.Add(new DeviceSnapshot(deviceId, CommandCatalog.CollectorHealthSnapshotId, captured,
            new JsonObject
            {
                ["errorCode"] = worst.Data["errorCode"]?.DeepClone(),
                ["lastAttemptUtc"] = commandHealth.Max(snapshot => snapshot.CapturedUtc).ToString("O"),
                ["lastSuccessUtc"] = lastSuccess,
                ["consecutiveFailures"] = worst.Data["consecutiveFailures"]?.DeepClone() ?? 0,
                ["state"] = worst.Data["state"]?.DeepClone() ?? "Healthy",
                ["commandId"] = worst.CommandId[prefix.Length..]
            }));
        return new CollectorStatePlan(snapshots, events, recoveries);
    }

    private static string CollectorCondition(string deviceId, string commandId) =>
        $"collector:{deviceId}:{commandId}";

    private static string AuthCondition(string deviceId) => $"collector-auth:{deviceId}";

    private static string NormalizeCollectorErrorCode(string code) => code switch
    {
        AgentErrorCodes.TcpTimeout or
        AgentErrorCodes.TelnetNegotiationFailed or
        AgentErrorCodes.LoginPromptNotFound or
        AgentErrorCodes.AuthFailed or
        AgentErrorCodes.CredentialCorrupt or
        AgentErrorCodes.CredentialUnavailable or
        AgentErrorCodes.CommandTimeout or
        AgentErrorCodes.PromptParseFailed or
        AgentErrorCodes.OutputLimitExceeded or
        AgentErrorCodes.IncompleteOutput or
        AgentErrorCodes.ParserUnsupported => code,
        _ => AgentErrorCodes.PromptParseFailed
    };

    private static DetectionPlan DetectChanges(
        SwitchOptions device,
        string commandId,
        DeviceSnapshot? previous,
        CollectedOutput current,
        bool suppressLogBufferReset)
    {
        if (previous is null)
        {
            return commandId == CommandIds.InterfaceStatus
                ? DetectInitialUplink(device, current)
                : DetectionPlan.Empty;
        }

        return commandId switch
        {
            CommandIds.InterfaceStatus => DetectUplink(device, previous.Data, current),
            "log_ram" => DetectLogs(device, previous.Data, current, suppressLogBufferReset),
            "system" => DetectRestart(device, previous.Data, current),
            _ => DetectionPlan.Empty
        };
    }

    private static DetectionPlan DetectUplink(SwitchOptions device, JsonObject previous, CollectedOutput current)
    {
        var wasUp = previous["uplinkOperationalUp"]?.GetValue<bool?>();
        var isUp = current.Structured["uplinkOperationalUp"]?.GetValue<bool?>();
        if (!wasUp.HasValue || !isUp.HasValue || wasUp == isUp)
        {
            return DetectionPlan.Empty;
        }

        var condition = $"uplink:{device.UplinkPort}";
        if (!isUp.Value)
        {
            return new DetectionPlan([new NewEvent(device.Id, EventSeverity.Critical, "uplink-down",
                "업링크 DOWN", $"포트 {device.UplinkPort}의 동작 상태가 UP에서 DOWN으로 바뀌었습니다.", EventState.New,
                condition, new Dictionary<string, string> { ["port"] = device.UplinkPort, ["from"] = "up", ["to"] = "down" },
                IsActiveCondition: true)], []);
        }

        var recovery = new NewEvent(device.Id, EventSeverity.Recovery, "uplink-recovered",
                "업링크 복구", $"포트 {device.UplinkPort}의 동작 상태가 DOWN에서 UP으로 바뀌었습니다.", EventState.Recovered,
                condition, new Dictionary<string, string> { ["port"] = device.UplinkPort, ["from"] = "down", ["to"] = "up" });
        return new DetectionPlan([], [new ConditionRecoveryRequest(device.Id, condition, current.CapturedUtc, recovery)]);
    }

    private static DetectionPlan DetectInitialUplink(SwitchOptions device, CollectedOutput current)
    {
        var isUp = current.Structured["uplinkOperationalUp"]?.GetValue<bool?>();
        if (isUp is not false)
        {
            return DetectionPlan.Empty;
        }

        return new DetectionPlan([new NewEvent(device.Id, EventSeverity.Critical, "uplink-down",
            "Initial uplink DOWN", $"Port {device.UplinkPort} is DOWN on the first valid status collection.",
            EventState.New, $"uplink:{device.UplinkPort}",
            new Dictionary<string, string>
            {
                ["port"] = device.UplinkPort,
                ["from"] = "unknown",
                ["to"] = "down"
            }, IsActiveCondition: true)], []);
    }

    private static bool WouldResetLogBaseline(DeviceSnapshot? previous, CollectedOutput current)
    {
        if (previous is null)
        {
            return false;
        }

        var known = previous.Data["entries"]?.AsArray()
            .Select(node => node?["id"]?.GetValue<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var currentIds = current.Structured["entries"]?.AsArray()
            .Select(node => node?["id"]?.GetValue<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal) ?? [];
        return currentIds.Count > 0 && (known.Count == 0 || !known.Overlaps(currentIds));
    }

    private static DetectionPlan DetectLogs(
        SwitchOptions device,
        JsonObject previous,
        CollectedOutput current,
        bool suppressLogBufferReset)
    {
        var known = previous["entries"]?.AsArray()
            .Select(node => node?["id"]?.GetValue<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var currentEntries = current.Structured["entries"]?.AsArray() ?? [];
        if (currentEntries.Count == 0)
        {
            return DetectionPlan.Empty;
        }

        if (known.Count == 0)
        {
            return suppressLogBufferReset ? DetectionPlan.Empty : LogBufferReset(device, current.CapturedUtc);
        }

        var currentIds = currentEntries
            .Select(node => node?["id"]?.GetValue<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);
        if (!known.Overlaps(currentIds))
        {
            return suppressLogBufferReset ? DetectionPlan.Empty : LogBufferReset(device, current.CapturedUtc);
        }

        var events = new List<NewEvent>();
        foreach (var node in currentEntries)
        {
            var id = node?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id) || known.Contains(id))
            {
                continue;
            }
            var message = node?["message"]?.GetValue<string>() ?? "New switch log entry.";
            var severityText = node?["severity"]?.GetValue<string>();
            var level = node?["level"]?.GetValue<int?>();
            var severity = severityText?.ToLowerInvariant() switch
            {
                "critical" or "alert" or "emergency" or "error" => EventSeverity.Critical,
                "warning" or "warn" => EventSeverity.Warning,
                "info" or "informational" or "notice" or "debug" => EventSeverity.Info,
                _ when level is <= 3 => EventSeverity.Critical,
                _ when level == 4 => EventSeverity.Warning,
                _ => EventSeverity.Info
            };
            events.Add(new NewEvent(device.Id, severity, "switch-log",
                "새 스위치 로그", message, EventState.New, $"log:{id}",
                new Dictionary<string, string> { ["logId"] = id }));
        }
        return new DetectionPlan(events, []);
    }

    private static DetectionPlan LogBufferReset(SwitchOptions device, DateTimeOffset capturedUtc) =>
        new([new NewEvent(device.Id, EventSeverity.Warning, "log-buffer-reset",
            "스위치 로그 기준선 재설정",
            "이전 로그 기준점을 찾을 수 없어 현재 로그를 새 기준선으로 설정했습니다.",
            EventState.New, $"log-buffer-reset:{capturedUtc.UtcTicks}")], []);

    private static DetectionPlan DetectRestart(SwitchOptions device, JsonObject previous, CollectedOutput current)
    {
        if (!HasRestarted(previous, current))
        {
            return DetectionPlan.Empty;
        }

        return new DetectionPlan([new NewEvent(device.Id, EventSeverity.Warning, "device-restart",
            "스위치 재시작 감지", "이전 점검보다 시스템 가동 시간이 감소했습니다.", EventState.New, "device:restart")], []);
    }

    private static bool HasRestarted(JsonObject previous, CollectedOutput current)
    {
        var oldUptime = TryGetLong(previous["uptimeSeconds"]);
        var newUptime = TryGetLong(current.Structured["uptimeSeconds"]);
        return oldUptime.HasValue && newUptime.HasValue && newUptime < oldUptime;
    }

    private static long? TryGetLong(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<long>(out var parsed) ? parsed : null;

    private static string Sanitize(string value) => new(value.Take(64).Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
}

public sealed class PollSchedulerService(
    AgentOptions options,
    CommandExecutionService execution,
    AgentRuntimeState runtime,
    SqliteAgentStore store,
    ILogger<PollSchedulerService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextDue = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _failureCounts = new(StringComparer.OrdinalIgnoreCase);
    private bool _storagePaused;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.EnablePolling)
        {
            logger.LogInformation("Automatic polling is disabled.");
            return;
        }

        foreach (var device in options.Switches)
        {
            foreach (var command in CommandCatalog.Registered.Values)
            {
                _nextDue[$"{device.Id}:{command.Id}"] = DateTimeOffset.MinValue;
            }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            runtime.TouchScheduler(now);
            var storage = await store.CheckReadinessAsync(stoppingToken);
            if (!storage.Ready)
            {
                if (!_storagePaused)
                {
                    logger.LogWarning("Scheduled polling paused because Agent storage is not ready with {ErrorCode}.",
                        storage.ErrorCode ?? AgentErrorCodes.StorageWriteFailed);
                    _storagePaused = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.SchedulerTickSeconds, 1, 60)),
                    stoppingToken);
                continue;
            }
            if (_storagePaused)
            {
                logger.LogInformation("Scheduled polling resumed after Agent storage integrity recovery.");
                _storagePaused = false;
            }

            var work = options.Switches.Select(device => new ScheduledDeviceWork(device,
                    CommandCatalog.Registered.Values
                        .Where(command => _nextDue[$"{device.Id}:{command.Id}"] <= now)
                        .ToArray()))
                .Where(item => item.Commands.Count > 0)
                .ToArray();
            await Parallel.ForEachAsync(work, new ParallelOptions
            {
                CancellationToken = stoppingToken,
                MaxDegreeOfParallelism = Math.Clamp(options.MaxConcurrentDevices, 1,
                    AgentOptions.MaximumConcurrentDeviceLimit)
            }, async (item, token) => await ProcessScheduledDeviceAsync(item, token));

            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.SchedulerTickSeconds, 1, 60)), stoppingToken);
        }
    }

    private async ValueTask ProcessScheduledDeviceAsync(
        ScheduledDeviceWork work,
        CancellationToken cancellationToken)
    {
        try
        {
            var results = await ExecuteBatchWithHeartbeatAsync(work.Device.Id,
                work.Commands.Select(command => command.Id).ToArray(), cancellationToken);
            var completedAt = DateTimeOffset.UtcNow;
            foreach (var command in work.Commands)
            {
                var key = $"{work.Device.Id}:{command.Id}";
                var result = results.Single(item => string.Equals(
                    item.CommandId, command.Id, StringComparison.OrdinalIgnoreCase));
                if (result.Success)
                {
                    _failureCounts[key] = 0;
                    _nextDue[key] = completedAt.Add(command.Interval);
                    continue;
                }

                var failures = _failureCounts.AddOrUpdate(key, 1, (_, previous) => previous + 1);
                _nextDue[key] = completedAt.Add(PollBackoffPolicy.ForError(result.ErrorCode, failures));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Scheduled batch collection failed for {DeviceId}.", work.Device.Id);
            var completedAt = DateTimeOffset.UtcNow;
            foreach (var command in work.Commands)
            {
                var key = $"{work.Device.Id}:{command.Id}";
                var failures = _failureCounts.AddOrUpdate(key, 1, (_, previous) => previous + 1);
                _nextDue[key] = completedAt.Add(PollBackoffPolicy.ForFailure(failures));
            }
        }
    }

    private async Task<IReadOnlyList<BatchCommandExecutionResult>> ExecuteBatchWithHeartbeatAsync(
        string deviceId,
        IReadOnlyList<string> commandIds,
        CancellationToken cancellationToken)
    {
        var executionTask = execution.ExecuteBatchAsync(deviceId, commandIds, "scheduler", cancellationToken);
        var heartbeatInterval = TimeSpan.FromSeconds(Math.Clamp(options.SchedulerTickSeconds, 1, 30));
        while (!executionTask.IsCompleted)
        {
            runtime.TouchScheduler(DateTimeOffset.UtcNow);
            var delay = Task.Delay(heartbeatInterval, cancellationToken);
            if (await Task.WhenAny(executionTask, delay) == executionTask)
            {
                break;
            }
            await delay;
        }
        runtime.TouchScheduler(DateTimeOffset.UtcNow);
        return await executionTask;
    }

    private sealed record ScheduledDeviceWork(
        SwitchOptions Device,
        IReadOnlyList<CommandDefinition> Commands);
}

public static class PollBackoffPolicy
{
    public static TimeSpan ForError(string? errorCode, int consecutiveFailures) => errorCode switch
    {
        AgentErrorCodes.ParserUnsupported => TimeSpan.FromHours(1),
        AgentErrorCodes.AuthFailed or AgentErrorCodes.CredentialCorrupt or AgentErrorCodes.CredentialUnavailable =>
            TimeSpan.FromMinutes(1),
        _ => ForFailure(consecutiveFailures)
    };

    public static TimeSpan ForFailure(int consecutiveFailures) => consecutiveFailures switch
    {
        <= 1 => TimeSpan.FromSeconds(10),
        2 => TimeSpan.FromSeconds(30),
        _ => TimeSpan.FromSeconds(60)
    };
}

public sealed class RetentionService(SqliteAgentStore store, ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await store.RunRetentionAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Retention pass failed.");
            }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }
}

public sealed class SimulationService(AgentOptions options, EventPublisher publisher)
{
    public async Task<StructuredEvent> SimulateAsync(string deviceId, string transition, CancellationToken token)
    {
        if (!options.MockMode && !options.EnableSimulator)
        {
            throw new AgentOperationException(AgentErrorCodes.CommandNotAllowed, "Simulator is disabled.", 404);
        }
        var device = options.Switches.SingleOrDefault(item => string.Equals(item.Id, deviceId, StringComparison.OrdinalIgnoreCase))
            ?? throw new AgentOperationException(AgentErrorCodes.DeviceNotFound, "Device was not found.", 404);
        var condition = $"simulated:uplink:{device.UplinkPort}";
        if (string.Equals(transition, "recover", StringComparison.OrdinalIgnoreCase))
        {
            var recoveredAt = DateTimeOffset.UtcNow;
            return await publisher.RecoverConditionAndPublishAsync(device.Id, condition, recoveredAt,
                new NewEvent(device.Id, EventSeverity.Recovery, "simulated-recovery",
                    "모의 복구", $"모의 모드에서 포트 {device.UplinkPort}가 복구되었습니다.", EventState.Recovered, condition), token);
        }

        if (string.Equals(transition, "down", StringComparison.OrdinalIgnoreCase))
        {
            return await publisher.PublishAsync(new NewEvent(device.Id, EventSeverity.Critical, "simulated-down",
                "모의 업링크 DOWN", $"모의 모드에서 포트 {device.UplinkPort}가 DOWN 상태입니다.", EventState.New, condition,
                IsActiveCondition: true), token);
        }

        if (string.Equals(transition, "log", StringComparison.OrdinalIgnoreCase))
        {
            return await publisher.PublishAsync(new NewEvent(device.Id, EventSeverity.Warning, "simulated-log",
                "모의 스위치 로그", "민감정보가 없는 모의 경고 로그를 생성했습니다.", EventState.New,
                $"simulated:log:{Guid.NewGuid():N}"), token);
        }

        throw new AgentOperationException(AgentErrorCodes.CommandNotAllowed, "Simulation transition is not registered.", 400);
    }
}
