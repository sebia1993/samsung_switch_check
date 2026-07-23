using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Execution;
using SamsungSwitchWatch.Agent.Security;
using SamsungSwitchWatch.Core.Diagnostics;
using SamsungSwitchWatch.Core.Profiles;

namespace SamsungSwitchWatch.Agent.Api;

public static class ApiEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app, AgentOptions options)
    {
        app.MapGet("/health/live", () => Results.Ok(new
        {
            status = "live",
            agentId = options.AgentId,
            utc = DateTimeOffset.UtcNow
        }));

        app.MapGet("/health/ready", () => Results.Ok(new
        {
            status = "ready",
            agentId = options.AgentId,
            apiVersion = 4,
            utc = DateTimeOffset.UtcNow
        }));

        app.MapGet("/api/v4/identity", (AgentIdentity identity) => Results.Ok(new
        {
            apiVersion = 4,
            agentId = options.AgentId,
            instanceId = identity.InstanceId,
            certificatePublicKeySha256 = identity.CertificatePublicKeySha256,
            protocol = "https",
            maxCommandsPerRequest = options.MaxCommandsPerRequest,
            maxOutputBytes = options.MaxOutputBytes
        }));

        app.MapPost("/api/v4/telnet/test", (
            TelnetApiRequest request,
            HttpContext context,
            TargetNetworkPolicy targetPolicy,
            DeviceProfileRegistry profiles,
            TelnetExecutionAdmission admission,
            IStatelessTelnetExecutor executor,
            CancellationToken cancellationToken) =>
            ExecuteAsync(
                request,
                isTest: true,
                context,
                targetPolicy,
                profiles,
                options,
                admission,
                executor,
                cancellationToken));

        app.MapPost("/api/v4/telnet/execute", (
            TelnetApiRequest request,
            HttpContext context,
            TargetNetworkPolicy targetPolicy,
            DeviceProfileRegistry profiles,
            TelnetExecutionAdmission admission,
            IStatelessTelnetExecutor executor,
            CancellationToken cancellationToken) =>
            ExecuteAsync(
                request,
                isTest: false,
                context,
                targetPolicy,
                profiles,
                options,
                admission,
                executor,
                cancellationToken));
    }

    private static async Task<IResult> ExecuteAsync(
        TelnetApiRequest request,
        bool isTest,
        HttpContext context,
        TargetNetworkPolicy targetPolicy,
        DeviceProfileRegistry profiles,
        AgentOptions options,
        TelnetExecutionAdmission admission,
        IStatelessTelnetExecutor executor,
        CancellationToken cancellationToken)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        var validated = TelnetRequestValidator.Validate(
            request,
            isTest,
            targetPolicy,
            profiles,
            options);
        var clientAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        await using var lease = await admission.EnterAsync(
            clientAddress,
            validated.Address,
            cancellationToken);
        try
        {
            return Results.Ok(await executor.ExecuteAsync(validated, cancellationToken));
        }
        catch (SwitchWatchException exception)
        {
            throw TelnetFailureMapper.Map(exception);
        }
    }
}

public sealed class ErrorHandlingMiddleware(
    RequestDelegate next,
    ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The Viewer disconnected or cancelled. Do not attempt to write a
            // response and do not log request data.
        }
        catch (AgentOperationException exception)
        {
            context.Response.StatusCode = exception.StatusCode;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new { code = exception.Code, message = exception.SafeMessage }
            }, cancellationToken: context.RequestAborted);
        }
        catch (BadHttpRequestException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    code = AgentErrorCodes.RequestInvalid,
                    message = "Request body is invalid."
                }
            }, cancellationToken: context.RequestAborted);
        }
        catch (Exception)
        {
            logger.LogError(
                "Unhandled Agent request failure with correlation id {TraceIdentifier}.",
                context.TraceIdentifier);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    code = "AGENT_INTERNAL_ERROR",
                    message = "Agent request failed.",
                    correlationId = context.TraceIdentifier
                }
            }, cancellationToken: context.RequestAborted);
        }
    }
}
