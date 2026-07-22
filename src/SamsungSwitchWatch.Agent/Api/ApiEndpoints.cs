using System.Collections.Concurrent;
using System.Text.Json;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Persistence;
using SamsungSwitchWatch.Agent.Polling;
using SamsungSwitchWatch.Agent.Queries;
using SamsungSwitchWatch.Core.Profiles;

namespace SamsungSwitchWatch.Agent.Api;

public sealed record DeviceCheckRunResult(
    string DeviceId,
    string Model,
    bool Success,
    string? ErrorCode,
    IReadOnlyList<BatchCommandExecutionResult> Commands);

public static class ApiEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app, AgentOptions options)
    {
        app.MapGet("/health", () => Results.Ok(new
        {
            status = "healthy",
            agentId = options.AgentId,
            mode = options.MockMode ? "mock" : "live",
            utc = DateTimeOffset.UtcNow
        }));

        app.MapGet("/health/live", () => Results.Ok(new
        {
            status = "live",
            agentId = options.AgentId,
            utc = DateTimeOffset.UtcNow
        }));

        app.MapGet("/health/ready", async (AgentReadinessService readiness, CancellationToken token) =>
        {
            var result = await readiness.CheckAsync(token);
            return Results.Json(result, statusCode: result.Ready
                ? StatusCodes.Status200OK
                : StatusCodes.Status503ServiceUnavailable);
        });

        app.MapGet("/api/v1/status", async (SqliteAgentStore store, CancellationToken token) =>
        {
            var eventSummary = await store.GetEventSummaryAsync(token);
            var snapshots = await store.GetAllSnapshotsAsync(token);
            return Results.Ok(new
            {
                agentId = options.AgentId,
                connected = true,
                mockMode = options.MockMode,
                deviceCount = options.Switches.Count,
                activeCritical = eventSummary.ActiveCritical,
                unacknowledged = eventSummary.Unacknowledged,
                lastEventSequence = eventSummary.LastSequence,
                lastCollectionUtc = snapshots.OrderByDescending(item => item.CapturedUtc).FirstOrDefault()?.CapturedUtc,
                utc = DateTimeOffset.UtcNow
            });
        });

        app.MapGet("/api/v1/devices", async (SqliteAgentStore store, CancellationToken token) =>
        {
            var snapshots = await store.GetAllSnapshotsAsync(token);
            var result = options.Switches.Select(device => new
            {
                id = device.Id,
                displayName = device.DisplayName,
                model = device.Model,
                uplinkPort = device.UplinkPort,
                collection = snapshots.Where(snapshot => snapshot.DeviceId == device.Id).Select(snapshot => new
                {
                    commandId = snapshot.CommandId,
                    capturedUtc = snapshot.CapturedUtc,
                    data = snapshot.Data
                }).ToArray()
            });
            return Results.Ok(result);
        });

        app.MapGet("/api/v1/events", async (long? after, int? limit, SqliteAgentStore store, CancellationToken token) =>
            Results.Ok(await store.GetEventsAfterAsync(after ?? 0, limit ?? 500, token)));

        app.MapGet("/api/v2/events/changes", async (
            long? after,
            int? limit,
            SqliteAgentStore store,
            CancellationToken token) =>
            Results.Ok(await store.GetEventChangesAsync(after ?? 0, limit ?? 500, token)));

        app.MapGet("/api/v2/events/recent", async (
            int? limit,
            SqliteAgentStore store,
            CancellationToken token) =>
            Results.Ok(await store.GetRecentEventsAsync(limit ?? 500, token)));

        app.MapGet("/api/v2/snapshot", async (
            SqliteAgentStore store,
            AgentReadinessService readiness,
            CancellationToken token) =>
        {
            var ready = await readiness.CheckAsync(token);
            var eventSummary = await store.GetEventSummaryAsync(token);
            var highWatermark = await store.GetChangeHighWatermarkAsync(token);
            var snapshots = await store.GetAllSnapshotsAsync(token);
            var devices = options.Switches.Select(device => new
            {
                id = device.Id,
                displayName = device.DisplayName,
                model = device.Model,
                uplinkPort = device.UplinkPort,
                collection = snapshots.Where(snapshot => snapshot.DeviceId == device.Id).Select(snapshot => new
                {
                    commandId = snapshot.CommandId,
                    capturedUtc = snapshot.CapturedUtc,
                    data = snapshot.Data
                }).ToArray()
            }).ToArray();
            return Results.Ok(new
            {
                agentId = options.AgentId,
                connected = true,
                ready = ready.Ready,
                readinessCode = ready.Code,
                mockMode = options.MockMode,
                deviceCount = options.Switches.Count,
                activeCritical = eventSummary.ActiveCritical,
                unacknowledged = eventSummary.Unacknowledged,
                highWatermark,
                lastCollectionUtc = snapshots.OrderByDescending(item => item.CapturedUtc).FirstOrDefault()?.CapturedUtc,
                utc = DateTimeOffset.UtcNow,
                devices
            });
        });

        app.MapGet("/api/v3/events/changes", async (
            long? after,
            int? limit,
            SqliteAgentStore store,
            CancellationToken token) =>
        {
            var page = await store.GetEventChangesAsync(after ?? 0, limit ?? 500, token);
            return Results.Ok(new
            {
                apiVersion = 3,
                page.HighWatermark,
                page.NextCursor,
                page.HasMore,
                page.Changes,
                page.ResetRequired,
                page.ResetCursor
            });
        });

        app.MapGet("/api/v3/events/recent", async (
            int? limit,
            SqliteAgentStore store,
            CancellationToken token) =>
        {
            var events = await store.GetRecentEventsAsync(limit ?? 500, token);
            return Results.Ok(new
            {
                apiVersion = 3,
                count = events.Count,
                events
            });
        });

        app.MapGet("/api/v3/snapshot", async (
            SqliteAgentStore store,
            AgentReadinessService readiness,
            AgentRuntimeState runtime,
            DeviceProfileRegistry profiles,
            CancellationToken token) =>
        {
            var storage = await store.CheckReadinessAsync(token);
            var ready = await readiness.CheckAsync(token);
            var eventSummary = await store.GetEventSummaryAsync(token);
            var highWatermark = await store.GetChangeHighWatermarkAsync(token);
            var snapshots = await store.GetAllSnapshotsAsync(token);
            var devices = BuildVersionThreeDevices(options, profiles, snapshots);
            return Results.Ok(new
            {
                apiVersion = 3,
                agentId = options.AgentId,
                mockMode = options.MockMode,
                utc = DateTimeOffset.UtcNow,
                lastCollectionUtc = snapshots
                    .Where(snapshot => CommandCatalog.Registered.ContainsKey(snapshot.CommandId))
                    .OrderByDescending(snapshot => snapshot.CapturedUtc)
                    .FirstOrDefault()?.CapturedUtc,
                counts = new
                {
                    configuredDevices = options.Switches.Count,
                    eventSummary.ActiveCritical,
                    eventSummary.Unacknowledged,
                    eventSummary.LastSequence,
                    eventChangeHighWatermark = highWatermark
                },
                channels = new
                {
                    agent = new
                    {
                        status = "connected",
                        runtime.StartedUtc,
                        runtime.SchedulerHeartbeatUtc
                    },
                    api = new { status = "available", version = 3 },
                    realtime = new
                    {
                        status = "available",
                        meaning = "agent-endpoint-available",
                        endpoint = "/hubs/events"
                    },
                    storage = new
                    {
                        ready = storage.Ready,
                        errorCode = storage.ErrorCode,
                        schemaVersion = storage.SchemaVersion,
                        integrityCheckedUtc = store.LastIntegrityCheckUtc
                    },
                    readiness = new
                    {
                        status = ready.Ready ? "ready" : "not-ready",
                        ready.Code,
                        ready.SchemaVersion,
                        ready.SchedulerHeartbeatUtc,
                        ready.CheckedUtc
                    }
                },
                features = new
                {
                    readOnlyQueries = new
                    {
                        enabled = options.EnableReadOnlyQueries,
                        maxCommandLength = options.ReadOnlyQueryMaxCommandLength,
                        maxOutputBytes = options.ReadOnlyQueryMaxOutputBytes
                    }
                },
                devices
            });
        });

        app.MapPost("/api/v3/read-only-queries", async (
            JsonElement request,
            HttpContext context,
            ReadOnlyQueryExecutionService execution,
            CancellationToken token) =>
        {
            var query = ParseReadOnlyQueryRequest(request);
            var viewerIp = context.Connection.RemoteIpAddress?.MapToIPv4().ToString() ?? "unknown";
            return Results.Ok(await execution.ExecuteAsync(
                query.DeviceId,
                query.Command,
                viewerIp,
                token));
        });

        app.MapPost("/api/v3/check-runs", async (
            JsonElement request,
            HttpContext context,
            CommandExecutionService execution,
            CancellationToken token) =>
        {
            var selection = ParseCheckRunRequest(request, options);
            var startedUtc = DateTimeOffset.UtcNow;
            var actor = Actor(context);
            var results = new ConcurrentDictionary<string, DeviceCheckRunResult>(StringComparer.OrdinalIgnoreCase);
            await Parallel.ForEachAsync(selection.Devices, new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = Math.Clamp(options.MaxConcurrentDevices, 1,
                    AgentOptions.MaximumConcurrentDeviceLimit)
            }, async (device, cancellationToken) =>
            {
                try
                {
                    var commands = await execution.ExecuteBatchAsync(device.Id, selection.CommandIds,
                        actor, cancellationToken);
                    results[device.Id] = new DeviceCheckRunResult(device.Id, device.Model,
                        commands.All(command => command.Success),
                        commands.FirstOrDefault(command => !command.Success)?.ErrorCode,
                        commands);
                }
                catch (AgentOperationException ex)
                {
                    results[device.Id] = new DeviceCheckRunResult(device.Id, device.Model, false, ex.Code, []);
                }
            });
            var completedUtc = DateTimeOffset.UtcNow;
            return Results.Ok(new
            {
                apiVersion = 3,
                startedUtc,
                completedUtc,
                deviceCount = selection.Devices.Count,
                commandCount = selection.CommandIds.Count,
                devices = selection.Devices.Select(device => results[device.Id]).ToArray()
            });
        });

        app.MapPost("/api/v1/events/{id}/ack", async (
            string id,
            HttpContext context,
            SqliteAgentStore store,
            EventPublisher publisher,
            CancellationToken token) =>
        {
            var updated = await publisher.AcknowledgeAsync(id, DateTimeOffset.UtcNow, token);
            if (updated is null)
            {
                return Results.NotFound(new { error = new { code = "EVENT_NOT_FOUND", message = "Event was not found." } });
            }
            await store.InsertAuditAsync(new AuditEntry(DateTimeOffset.UtcNow, "event-ack",
                Actor(context), updated.DeviceId, "success", "Event acknowledged."), token);
            return Results.Ok(updated);
        });

        app.MapPost("/api/v1/commands/{deviceId}/{commandId}", async (
            string deviceId,
            string commandId,
            HttpContext context,
            CommandExecutionService execution,
            CancellationToken token) =>
            Results.Ok(await execution.ExecuteAsync(deviceId, commandId, Actor(context), token)));

        app.MapHub<AgentEventsHub>("/hubs/events");

        if ((options.MockMode || app.Environment.IsDevelopment()) && options.EnableSimulator)
        {
            app.MapPost("/api/dev/simulate/{deviceId}/{transition}", async (
                string deviceId,
                string transition,
                SimulationService simulator,
                CancellationToken token) => Results.Ok(await simulator.SimulateAsync(deviceId, transition, token)));
        }
    }

    private static string Actor(HttpContext context) => "viewer-http";

    private static IReadOnlyList<object> BuildVersionThreeDevices(
        AgentOptions options,
        DeviceProfileRegistry profiles,
        IReadOnlyList<DeviceSnapshot> snapshots)
    {
        return options.Switches.Select(device =>
        {
            profiles.TryGet(device.Model, out var profile);
            var deviceSnapshots = snapshots.Where(snapshot =>
                    string.Equals(snapshot.DeviceId, device.Id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var collections = deviceSnapshots
                .Where(snapshot => CommandCatalog.Registered.ContainsKey(snapshot.CommandId))
                .OrderBy(snapshot => snapshot.CommandId, StringComparer.OrdinalIgnoreCase)
                .Select(snapshot => new
                {
                    snapshot.CommandId,
                    snapshot.CapturedUtc,
                    data = snapshot.Data
                }).ToArray();
            var aggregate = deviceSnapshots.FirstOrDefault(snapshot => string.Equals(snapshot.CommandId,
                CommandCatalog.CollectorHealthSnapshotId, StringComparison.OrdinalIgnoreCase));
            var capabilities = CommandCatalog.Registered.Values.Select(command =>
            {
                ReadOnlyCommandDefinition? profileCommand = null;
                var profileSupported = profile is not null && profile.TryGetCommand(command.Id, out profileCommand);
                var health = deviceSnapshots.FirstOrDefault(snapshot => string.Equals(snapshot.CommandId,
                    CommandCatalog.CollectorHealthSnapshotIdFor(command.Id), StringComparison.OrdinalIgnoreCase));
                var collection = deviceSnapshots.FirstOrDefault(snapshot => string.Equals(snapshot.CommandId,
                    command.Id, StringComparison.OrdinalIgnoreCase));
                var state = profileSupported
                    ? health?.Data["state"]?.GetValue<string>() ?? "Initializing"
                    : "Unsupported";
                var candidates = profileCommand?.CandidateCommands ?? [command.Cli];
                var primaryCli = candidates.First();
                var lastSuccessfulCli = collection?.Data["collectorCli"]?.GetValue<string>();
                var selectedCli = string.Equals(state, "Healthy", StringComparison.OrdinalIgnoreCase)
                    ? lastSuccessfulCli
                    : null;
                if (string.IsNullOrWhiteSpace(selectedCli) && profileSupported &&
                    string.Equals(state, "Healthy", StringComparison.OrdinalIgnoreCase))
                {
                    selectedCli = primaryCli;
                }
                if (string.IsNullOrWhiteSpace(lastSuccessfulCli) &&
                    string.Equals(state, "Healthy", StringComparison.OrdinalIgnoreCase))
                {
                    lastSuccessfulCli = selectedCli;
                }
                return new
                {
                    commandId = command.Id,
                    cli = primaryCli,
                    primaryCli,
                    selectedCli,
                    lastSuccessfulCli,
                    candidateClis = candidates,
                    fallbackUsed = !string.IsNullOrWhiteSpace(selectedCli) &&
                                   !string.Equals(selectedCli, primaryCli, StringComparison.OrdinalIgnoreCase),
                    supported = profileSupported && !string.Equals(state, "Unsupported", StringComparison.Ordinal),
                    state,
                    errorCode = health?.Data["errorCode"]?.GetValue<string>(),
                    lastAttemptUtc = health?.Data["lastAttemptUtc"]?.GetValue<string>(),
                    lastSuccessUtc = health?.Data["lastSuccessUtc"]?.GetValue<string>()
                };
            }).ToArray();
            return (object)new
            {
                id = device.Id,
                device.DisplayName,
                device.Model,
                device.UplinkPort,
                lastCollectionUtc = collections.OrderByDescending(collection => collection.CapturedUtc)
                    .FirstOrDefault()?.CapturedUtc,
                collectionHealth = new
                {
                    state = aggregate?.Data["state"]?.GetValue<string>() ?? "Initializing",
                    errorCode = aggregate?.Data["errorCode"]?.GetValue<string>(),
                    lastAttemptUtc = aggregate?.Data["lastAttemptUtc"]?.GetValue<string>(),
                    lastSuccessUtc = aggregate?.Data["lastSuccessUtc"]?.GetValue<string>()
                },
                capabilities,
                collections
            };
        }).ToArray();
    }

    private static CheckRunSelection ParseCheckRunRequest(JsonElement request, AgentOptions options)
    {
        if (request.ValueKind != JsonValueKind.Object)
        {
            throw new AgentOperationException("REQUEST_INVALID", "Check run request must be a JSON object.", 400);
        }

        var allowed = new HashSet<string>(["deviceIds", "commandIds"], StringComparer.Ordinal);
        if (request.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
        {
            throw new AgentOperationException("REQUEST_INVALID",
                "Check run accepts only deviceIds and commandIds.", 400);
        }

        var deviceIds = ReadRequiredStringArray(request, "deviceIds");
        var commandIds = ReadRequiredStringArray(request, "commandIds");
        if (deviceIds.Count > options.Switches.Count || commandIds.Count > CommandCatalog.Registered.Count)
        {
            throw new AgentOperationException("REQUEST_INVALID", "Check run contains too many ids.", 400);
        }
        var devices = new List<SwitchOptions>(deviceIds.Count);
        foreach (var deviceId in deviceIds)
        {
            var device = options.Switches.FirstOrDefault(item =>
                string.Equals(item.Id, deviceId, StringComparison.OrdinalIgnoreCase))
                ?? throw new AgentOperationException(AgentErrorCodes.DeviceNotFound, "Device was not found.", 404);
            devices.Add(device);
        }

        foreach (var commandId in commandIds)
        {
            if (!CommandCatalog.TryGet(commandId, out _))
            {
                throw new AgentOperationException(AgentErrorCodes.CommandNotAllowed,
                    "Command id is not registered.", 400);
            }
        }
        return new CheckRunSelection(devices, commandIds);
    }

    private static ReadOnlyQueryRequest ParseReadOnlyQueryRequest(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object)
        {
            throw new AgentOperationException("REQUEST_INVALID",
                "Read-only query request must be a JSON object.", 400);
        }

        var allowed = new HashSet<string>(["deviceId", "command"], StringComparer.Ordinal);
        if (request.EnumerateObject().Any(property => !allowed.Contains(property.Name)) ||
            !request.TryGetProperty("deviceId", out var deviceIdElement) ||
            deviceIdElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(deviceIdElement.GetString()) ||
            deviceIdElement.GetString()!.Length > 64 ||
            deviceIdElement.GetString()!.Any(character =>
                !char.IsLetterOrDigit(character) && character is not '-' and not '_') ||
            !request.TryGetProperty("command", out var commandElement) ||
            commandElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(commandElement.GetString()))
        {
            throw new AgentOperationException("REQUEST_INVALID",
                "Read-only query accepts only non-empty deviceId and command strings.", 400);
        }

        return new ReadOnlyQueryRequest(deviceIdElement.GetString()!, commandElement.GetString()!);
    }

    private static IReadOnlyList<string> ReadRequiredStringArray(JsonElement request, string propertyName)
    {
        if (!request.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            throw new AgentOperationException("REQUEST_INVALID", $"{propertyName} must be an array.", 400);
        }

        var values = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
            {
                throw new AgentOperationException("REQUEST_INVALID", $"{propertyName} contains an invalid id.", 400);
            }
            values.Add(element.GetString()!);
        }
        var distinct = values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinct.Length == 0 || distinct.Length != values.Count)
        {
            throw new AgentOperationException("REQUEST_INVALID", $"{propertyName} must contain unique ids.", 400);
        }
        return distinct;
    }

    private sealed record CheckRunSelection(
        IReadOnlyList<SwitchOptions> Devices,
        IReadOnlyList<string> CommandIds);

    private sealed record ReadOnlyQueryRequest(string DeviceId, string Command);
}

public sealed class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AgentOperationException ex)
        {
            context.Response.StatusCode = ex.StatusCode;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new { code = ex.Code, message = ex.SafeMessage }
            }, cancellationToken: context.RequestAborted);
        }
        catch (BadHttpRequestException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new { code = "REQUEST_INVALID", message = "Request body is invalid." }
            }, cancellationToken: context.RequestAborted);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled Agent request failure with correlation id {TraceIdentifier}.", context.TraceIdentifier);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new { code = "AGENT_INTERNAL_ERROR", message = "Agent request failed.", correlationId = context.TraceIdentifier }
            }, cancellationToken: context.RequestAborted);
        }
    }
}
