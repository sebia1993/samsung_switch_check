using System.Net;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;

namespace SamsungSwitchWatch.Agent.Api;

public sealed class ViewerIpAccessMiddleware(
    RequestDelegate next,
    AgentOptions options)
{
    private readonly IPAddress? _allowedViewerAddress =
        string.IsNullOrWhiteSpace(options.AllowedViewerIpv4)
            ? null
            : IPAddress.Parse(options.AllowedViewerIpv4);

    public async Task InvokeAsync(HttpContext context)
    {
        var remoteAddress = Normalize(context.Connection.RemoteIpAddress);
        if (IsAllowed(context.Request.Path, remoteAddress))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        await context.Response.WriteAsJsonAsync(new
        {
            error = new
            {
                code = AgentErrorCodes.ClientNotAllowed,
                message = "This Viewer is not allowed to access the Agent."
            }
        }, cancellationToken: context.RequestAborted);
    }

    private bool IsAllowed(PathString path, IPAddress? remoteAddress)
    {
        if (remoteAddress is null)
        {
            return false;
        }

        if (_allowedViewerAddress is not null &&
            remoteAddress.Equals(_allowedViewerAddress))
        {
            return true;
        }

        if (!IPAddress.IsLoopback(remoteAddress))
        {
            return false;
        }

        return options.MockMode ||
               path.Equals("/health/live", StringComparison.OrdinalIgnoreCase) ||
               path.Equals("/health/ready", StringComparison.OrdinalIgnoreCase);
    }

    private static IPAddress? Normalize(IPAddress? address) =>
        address?.IsIPv4MappedToIPv6 == true
            ? address.MapToIPv4()
            : address;
}
