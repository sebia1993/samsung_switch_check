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

public static class CommandCatalog
{
    public const string CollectorHealthSnapshotId = "collector_health";

    public static readonly IReadOnlyDictionary<string, CommandDefinition> Registered =
        new Dictionary<string, CommandDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["version"] = new("version", "show version", TimeSpan.FromHours(1)),
            ["system"] = new("system", "show system", TimeSpan.FromMinutes(5)),
            ["log_ram"] = new("log_ram", "show log ram", TimeSpan.FromMinutes(1)),
            [CommandIds.InterfaceStatus] = new(CommandIds.InterfaceStatus, "show interfaces status", TimeSpan.FromMinutes(1))
        };

    public static bool TryGet(string id, out CommandDefinition definition) => Registered.TryGetValue(id, out definition!);
}

public interface IDeviceCollector
{
    Task<CollectedOutput> CollectAsync(SwitchOptions device, CommandDefinition command, CancellationToken cancellationToken);
}

public sealed class MockDeviceCollector : IDeviceCollector
{
    private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.OrdinalIgnoreCase);
    private readonly DateTimeOffset _started = DateTimeOffset.UtcNow;

    public Task<CollectedOutput> CollectAsync(SwitchOptions device, CommandDefinition command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var call = _calls.AddOrUpdate(command.Id, 1, (_, value) => value + 1);
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
    DeviceCommandProfile profile,
    ICredentialVault credentials) : IDeviceCollector
{
    public async Task<CollectedOutput> CollectAsync(SwitchOptions device, CommandDefinition command, CancellationToken cancellationToken)
    {
        var credential = await credentials.GetAsync(device.CredentialId, cancellationToken)
            ?? throw new AgentOperationException(AgentErrorCodes.AuthFailed,
                "Switch credential is not configured on the Agent.", 503);
        try
        {
            var session = await telnet.ExecuteRegisteredAsync(
                new TelnetEndpoint(device.Host, device.Port),
                new TelnetCredentials(credential.Username, credential.Password),
                profile,
                [command.Id],
                cancellationToken);
            var parsed = SamsungSnapshotParser.Parse(device.Id, session.Outputs, session.CompletedAt);
            var output = session.Outputs.Single();
            var structured = ConvertSnapshot(command.Id, device.UplinkPort, parsed);
            var status = parsed.Issues.FirstOrDefault()?.Error.Code ?? "OK";
            return new CollectedOutput(device.Id, command.Id, output.CollectedAt, structured, output.RawOutput, status);
        }
        catch (SwitchWatchException ex)
        {
            throw new AgentOperationException(NormalizeCode(ex.Error.Code), ex.Error.Message, ex.Error.IsRetryable ? 503 : 400);
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

        var issue = snapshot.Issues.FirstOrDefault();
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
        AgentErrorCodes.ParserUnsupported => code,
        _ => AgentErrorCodes.PromptParseFailed
    };
}

public sealed class AgentEventsHub : Hub;

public sealed class EventPublisher(SqliteAgentStore store, IHubContext<AgentEventsHub> hub)
{
    public async Task<StructuredEvent> PublishAsync(NewEvent item, CancellationToken cancellationToken)
    {
        var created = await store.InsertEventAsync(item, cancellationToken);
        await hub.Clients.All.SendAsync("eventReceived", created, cancellationToken);
        return created;
    }

    public Task PublishUpdateAsync(StructuredEvent item, CancellationToken cancellationToken) =>
        hub.Clients.All.SendAsync("eventUpdated", item, cancellationToken);
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
            var previous = await store.GetSnapshotAsync(device.Id, command.Id, cancellationToken);
            var previousHealth = await store.GetSnapshotAsync(device.Id, CommandCatalog.CollectorHealthSnapshotId, cancellationToken);
            CollectedOutput output;
            try
            {
                output = await collector.CollectAsync(device, command, cancellationToken);
            }
            catch (AgentOperationException ex)
            {
                var safeCode = NormalizeCollectorErrorCode(ex.Code);
                await RecordCollectorFailureAsync(device, previousHealth, safeCode, cancellationToken);
                logger.LogWarning("Collection failed for {DeviceId}/{CommandId} with {ErrorCode}.", device.Id, command.Id, safeCode);
                await store.InsertAuditAsync(new AuditEntry(DateTimeOffset.UtcNow, "command", actor, device.Id,
                    "failed", $"Error code: {safeCode}"), cancellationToken);
                throw;
            }

            await RecordCollectorSuccessAsync(device, previousHealth, output.CapturedUtc, cancellationToken);
            await store.InsertRawAsync(device.Id, command.Id, output.CapturedUtc, output.RawOutput, cancellationToken);
            var events = await DetectChangesAsync(device, command.Id, previous, output, cancellationToken);
            await store.UpsertSnapshotAsync(new DeviceSnapshot(device.Id, command.Id, output.CapturedUtc, output.Structured), cancellationToken);
            await store.InsertAuditAsync(new AuditEntry(DateTimeOffset.UtcNow, "command", actor, device.Id,
                "success", $"Registered command id: {command.Id}"), cancellationToken);
            return new CommandExecutionResult(device.Id, command.Id, output.CapturedUtc, output.CollectorStatus,
                output.Structured, events);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task RecordCollectorFailureAsync(
        SwitchOptions device,
        DeviceSnapshot? previousHealth,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var attempted = DateTimeOffset.UtcNow;
        var wasFailed = !string.IsNullOrWhiteSpace(previousHealth?.Data["errorCode"]?.GetValue<string>());
        await store.UpsertSnapshotAsync(new DeviceSnapshot(device.Id, CommandCatalog.CollectorHealthSnapshotId, attempted,
            new JsonObject
            {
                ["errorCode"] = errorCode,
                ["lastAttemptUtc"] = attempted.ToString("O")
            }), cancellationToken);

        if (!wasFailed)
        {
            await publisher.PublishAsync(new NewEvent(device.Id, EventSeverity.Critical, "collector-failed",
                "스위치 수집 실패", $"수집에 실패했습니다. 오류 코드: {errorCode}", EventState.New,
                CollectorCondition(device.Id), new Dictionary<string, string> { ["errorCode"] = errorCode }), cancellationToken);
        }
    }

    private async Task RecordCollectorSuccessAsync(
        SwitchOptions device,
        DeviceSnapshot? previousHealth,
        DateTimeOffset attempted,
        CancellationToken cancellationToken)
    {
        var wasFailed = !string.IsNullOrWhiteSpace(previousHealth?.Data["errorCode"]?.GetValue<string>());
        var previousCode = previousHealth?.Data["errorCode"]?.GetValue<string>();
        await store.UpsertSnapshotAsync(new DeviceSnapshot(device.Id, CommandCatalog.CollectorHealthSnapshotId, attempted,
            new JsonObject
            {
                ["errorCode"] = null,
                ["lastAttemptUtc"] = attempted.ToString("O")
            }), cancellationToken);

        if (wasFailed)
        {
            await store.MarkConditionRecoveredAsync(device.Id, CollectorCondition(device.Id), attempted, cancellationToken);
            var details = string.IsNullOrWhiteSpace(previousCode)
                ? null
                : new Dictionary<string, string> { ["previousErrorCode"] = NormalizeCollectorErrorCode(previousCode) };
            await publisher.PublishAsync(new NewEvent(device.Id, EventSeverity.Recovery, "collector-recovered",
                "스위치 수집 복구", "Agent가 스위치 상태를 다시 정상적으로 수집합니다.", EventState.Recovered,
                CollectorCondition(device.Id), details), cancellationToken);
        }
    }

    private static string CollectorCondition(string deviceId) => $"collector:{deviceId}";

    private static string NormalizeCollectorErrorCode(string code) => code switch
    {
        AgentErrorCodes.TcpTimeout or
        AgentErrorCodes.TelnetNegotiationFailed or
        AgentErrorCodes.LoginPromptNotFound or
        AgentErrorCodes.AuthFailed or
        AgentErrorCodes.CommandTimeout or
        AgentErrorCodes.PromptParseFailed or
        AgentErrorCodes.OutputLimitExceeded or
        AgentErrorCodes.ParserUnsupported => code,
        _ => AgentErrorCodes.PromptParseFailed
    };

    private async Task<int> DetectChangesAsync(
        SwitchOptions device,
        string commandId,
        DeviceSnapshot? previous,
        CollectedOutput current,
        CancellationToken cancellationToken)
    {
        if (previous is null)
        {
            return 0;
        }

        return commandId switch
        {
            CommandIds.InterfaceStatus => await DetectUplinkAsync(device, previous.Data, current, cancellationToken),
            "log_ram" => await DetectLogsAsync(device, previous.Data, current, cancellationToken),
            "system" => await DetectRestartAsync(device, previous.Data, current, cancellationToken),
            _ => 0
        };
    }

    private async Task<int> DetectUplinkAsync(SwitchOptions device, JsonObject previous, CollectedOutput current, CancellationToken token)
    {
        var wasUp = previous["uplinkOperationalUp"]?.GetValue<bool?>();
        var isUp = current.Structured["uplinkOperationalUp"]?.GetValue<bool?>();
        if (!wasUp.HasValue || !isUp.HasValue || wasUp == isUp)
        {
            return 0;
        }

        var condition = $"uplink:{device.UplinkPort}";
        if (!isUp.Value)
        {
            await publisher.PublishAsync(new NewEvent(device.Id, EventSeverity.Critical, "uplink-down",
                "업링크 DOWN", $"포트 {device.UplinkPort}의 동작 상태가 UP에서 DOWN으로 바뀌었습니다.", EventState.New,
                condition, new Dictionary<string, string> { ["port"] = device.UplinkPort, ["from"] = "up", ["to"] = "down" }), token);
        }
        else
        {
            await store.MarkConditionRecoveredAsync(device.Id, condition, current.CapturedUtc, token);
            await publisher.PublishAsync(new NewEvent(device.Id, EventSeverity.Recovery, "uplink-recovered",
                "업링크 복구", $"포트 {device.UplinkPort}의 동작 상태가 DOWN에서 UP으로 바뀌었습니다.", EventState.Recovered,
                condition, new Dictionary<string, string> { ["port"] = device.UplinkPort, ["from"] = "down", ["to"] = "up" }), token);
        }
        return 1;
    }

    private async Task<int> DetectLogsAsync(SwitchOptions device, JsonObject previous, CollectedOutput current, CancellationToken token)
    {
        var known = previous["entries"]?.AsArray()
            .Select(node => node?["id"]?.GetValue<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var created = 0;
        foreach (var node in current.Structured["entries"]?.AsArray() ?? [])
        {
            var id = node?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id) || known.Contains(id))
            {
                continue;
            }
            var message = node?["message"]?.GetValue<string>() ?? "New switch log entry.";
            await publisher.PublishAsync(new NewEvent(device.Id, EventSeverity.Warning, "switch-log",
                "새 스위치 로그", message, EventState.New, $"log:{id}",
                new Dictionary<string, string> { ["logId"] = id }), token);
            created++;
        }
        return created;
    }

    private async Task<int> DetectRestartAsync(SwitchOptions device, JsonObject previous, CollectedOutput current, CancellationToken token)
    {
        var oldUptime = previous["uptimeSeconds"]?.GetValue<long?>();
        var newUptime = current.Structured["uptimeSeconds"]?.GetValue<long?>();
        if (!oldUptime.HasValue || !newUptime.HasValue || newUptime >= oldUptime)
        {
            return 0;
        }

        await publisher.PublishAsync(new NewEvent(device.Id, EventSeverity.Critical, "device-restart",
            "스위치 재시작 감지", "이전 점검보다 시스템 가동 시간이 감소했습니다.", EventState.New, "device:restart"), token);
        return 1;
    }

    private static string Sanitize(string value) => new(value.Take(64).Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
}

public sealed class PollSchedulerService(
    AgentOptions options,
    CommandExecutionService execution,
    ILogger<PollSchedulerService> logger) : BackgroundService
{
    private readonly Dictionary<string, DateTimeOffset> _nextDue = new(StringComparer.OrdinalIgnoreCase);

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
            foreach (var device in options.Switches)
            {
                foreach (var command in CommandCatalog.Registered.Values)
                {
                    var key = $"{device.Id}:{command.Id}";
                    if (_nextDue[key] > now)
                    {
                        continue;
                    }

                    try
                    {
                        await execution.ExecuteAsync(device.Id, command.Id, "scheduler", stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Scheduled collection failed for {DeviceId}/{CommandId}.", device.Id, command.Id);
                    }
                    finally
                    {
                        _nextDue[key] = DateTimeOffset.UtcNow.Add(command.Interval);
                    }
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(options.SchedulerTickSeconds, 1, 60)), stoppingToken);
        }
    }
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

public sealed class SimulationService(AgentOptions options, SqliteAgentStore store, EventPublisher publisher)
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
            await store.MarkConditionRecoveredAsync(device.Id, condition, DateTimeOffset.UtcNow, token);
            return await publisher.PublishAsync(new NewEvent(device.Id, EventSeverity.Recovery, "simulated-recovery",
                "모의 복구", $"모의 모드에서 포트 {device.UplinkPort}가 복구되었습니다.", EventState.Recovered, condition), token);
        }

        if (string.Equals(transition, "down", StringComparison.OrdinalIgnoreCase))
        {
            return await publisher.PublishAsync(new NewEvent(device.Id, EventSeverity.Critical, "simulated-down",
                "모의 업링크 DOWN", $"모의 모드에서 포트 {device.UplinkPort}가 DOWN 상태입니다.", EventState.New, condition), token);
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
