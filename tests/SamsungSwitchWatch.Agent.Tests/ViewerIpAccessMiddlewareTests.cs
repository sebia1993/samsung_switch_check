using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using SamsungSwitchWatch.Agent.Api;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;

namespace SamsungSwitchWatch.Agent.Tests;

public sealed class ViewerIpAccessMiddlewareTests
{
    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.10")]
    [InlineData("::ffff:192.168.1.10")]
    public async Task Runtime_AllowsRfc1918ViewerSources(string remoteAddress)
    {
        var result = await InvokeAsync(
            IPAddress.Parse(remoteAddress),
            "/api/v4/identity");

        Assert.True(result.NextInvoked);
        Assert.Equal(StatusCodes.Status204NoContent, result.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1", "/api/v4/identity")]
    [InlineData("::1", "/api/v4/telnet/test")]
    [InlineData("127.0.0.1", "/health/ready")]
    public async Task Runtime_AllowsLoopbackOnEveryEndpoint(
        string remoteAddress,
        string path)
    {
        var result = await InvokeAsync(IPAddress.Parse(remoteAddress), path);

        Assert.True(result.NextInvoked);
        Assert.Equal(StatusCodes.Status204NoContent, result.StatusCode);
    }

    [Theory]
    [InlineData("192.0.2.10")]
    [InlineData("172.15.255.254")]
    [InlineData("172.32.0.1")]
    [InlineData("169.254.10.20")]
    [InlineData("2001:db8::10")]
    public async Task Runtime_DeniesNonPrivateNonLoopbackSourcesWithoutDisclosure(
        string remoteAddress)
    {
        var result = await InvokeAsync(
            IPAddress.Parse(remoteAddress),
            "/api/v4/identity");

        Assert.False(result.NextInvoked);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal(AgentErrorCodes.ClientNotAllowed, ReadErrorCode(result.Body));
        Assert.DoesNotContain(remoteAddress, result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Runtime_DeniesRequestWithoutRemoteAddress()
    {
        var result = await InvokeAsync(null, "/api/v4/identity");

        Assert.False(result.NextInvoked);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal(AgentErrorCodes.ClientNotAllowed, ReadErrorCode(result.Body));
    }

    [Fact]
    public async Task Runtime_IgnoresForwardedForHeader()
    {
        var denied = await InvokeAsync(
            IPAddress.Parse("192.0.2.10"),
            "/api/v4/identity",
            forwardedFor: "192.168.10.25");
        var allowed = await InvokeAsync(
            IPAddress.Parse("192.168.10.25"),
            "/api/v4/identity",
            forwardedFor: "192.0.2.10");

        Assert.False(denied.NextInvoked);
        Assert.True(allowed.NextInvoked);
    }

    [Fact]
    public void Configuration_IgnoresLegacyViewerAndTargetAuthorities()
    {
        var folder = NewTemporaryFolder();
        try
        {
            var options = new AgentOptions
            {
                ListenUrl = "https://0.0.0.0:18443",
                DataDirectory = folder,
                AllowedViewerIpv4 = "not-an-address",
                AllowedTargetCidrs = ["203.0.113.0/24"]
            };

            AgentOptionsValidator.ValidateAndNormalize(options, folder);

            Assert.Equal(string.Empty, options.AllowedViewerIpv4);
            Assert.Equal(
                AgentOptions.AutomaticPrivateNetworkCidrs,
                options.AllowedTargetCidrs);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static async Task<InvocationResult> InvokeAsync(
        IPAddress? remoteAddress,
        string path,
        string? forwardedFor = null)
    {
        var nextInvoked = false;
        var middleware = new ViewerIpAccessMiddleware(context =>
        {
            nextInvoked = true;
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remoteAddress;
        context.Request.Path = path;
        if (forwardedFor is not null)
        {
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        }

        await using var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.InvokeAsync(context);

        body.Position = 0;
        return new InvocationResult(
            nextInvoked,
            context.Response.StatusCode,
            Encoding.UTF8.GetString(body.ToArray()));
    }

    private static string ReadErrorCode(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement
            .GetProperty("error")
            .GetProperty("code")
            .GetString()!;
    }

    private static string NewTemporaryFolder()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "SamsungSwitchWatch-AgentViewerIpTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    private sealed record InvocationResult(
        bool NextInvoked,
        int StatusCode,
        string Body);
}
