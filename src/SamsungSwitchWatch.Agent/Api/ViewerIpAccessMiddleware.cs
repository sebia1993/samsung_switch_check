using System.Net;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;

namespace SamsungSwitchWatch.Agent.Api;

public sealed class ViewerIpAccessMiddleware(
    RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var remoteAddress = Normalize(context.Connection.RemoteIpAddress);
        if (IsAllowed(remoteAddress))
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

    private static bool IsAllowed(IPAddress? remoteAddress) =>
        remoteAddress is not null &&
        (IPAddress.IsLoopback(remoteAddress) ||
         Ipv4Cidr.IsRfc1918Address(remoteAddress));

    private static IPAddress? Normalize(IPAddress? address) =>
        address?.IsIPv4MappedToIPv6 == true
            ? address.MapToIPv4()
            : address;
}
