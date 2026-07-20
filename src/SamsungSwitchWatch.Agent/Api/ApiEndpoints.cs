using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Persistence;
using SamsungSwitchWatch.Agent.Polling;
using SamsungSwitchWatch.Agent.Security;

namespace SamsungSwitchWatch.Agent.Api;

public sealed record PairingExchangeRequest(string Code);

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

        app.MapGet("/api/v1/certificate/fingerprint", (CertificateStatusService certificates) =>
            Results.Ok(certificates.Status));

        app.MapPost("/api/v1/pairing/bootstrap", async (
            HttpContext context,
            PairingService pairing,
            SqliteAgentStore store,
            CancellationToken token) =>
        {
            if (!options.AllowRemotePairingBootstrap &&
                context.Connection.RemoteIpAddress is { } remote &&
                !System.Net.IPAddress.IsLoopback(remote))
            {
                return Results.Json(new
                {
                    error = new { code = AgentErrorCodes.AuthFailed, message = "Pairing bootstrap is local-only." }
                }, statusCode: StatusCodes.Status403Forbidden);
            }
            var created = await pairing.CreateCodeAsync(token);
            await store.InsertAuditAsync(new AuditEntry(DateTimeOffset.UtcNow, "pairing-bootstrap", "local", null,
                "success", "One-time pairing code created."), token);
            return Results.Ok(new { code = created.Code, expiresUtc = created.ExpiresUtc });
        });

        app.MapPost("/api/v1/pairing/exchange", async (
            PairingExchangeRequest request,
            PairingService pairing,
            SqliteAgentStore store,
            CancellationToken token) =>
        {
            var bearer = await pairing.ExchangeAsync(request.Code, token);
            await store.InsertAuditAsync(new AuditEntry(DateTimeOffset.UtcNow, "pairing-exchange", "viewer", null,
                "success", "One-time pairing completed."), token);
            return Results.Ok(new { token = bearer, tokenType = "Bearer" });
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

        app.MapPost("/api/v1/events/{id}/ack", async (
            string id,
            HttpContext context,
            SqliteAgentStore store,
            EventPublisher publisher,
            CancellationToken token) =>
        {
            var updated = await store.AcknowledgeEventAsync(id, DateTimeOffset.UtcNow, token);
            if (updated is null)
            {
                return Results.NotFound(new { error = new { code = "EVENT_NOT_FOUND", message = "Event was not found." } });
            }
            await store.InsertAuditAsync(new AuditEntry(DateTimeOffset.UtcNow, "event-ack",
                Actor(context), updated.DeviceId, "success", "Event acknowledged."), token);
            await publisher.PublishUpdateAsync(updated, token);
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

    private static string Actor(HttpContext context) => context.Items["actor"] as string ?? "unknown";
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
