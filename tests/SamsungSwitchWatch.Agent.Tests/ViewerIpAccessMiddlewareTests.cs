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
    [InlineData("192.168.10.25")]
    [InlineData("::ffff:192.168.10.25")]
    public async Task Production_AllowsExactViewerAddressIncludingMappedIpv4(
        string remoteAddress)
    {
        var result = await InvokeAsync(
            ProductionOptions(),
            IPAddress.Parse(remoteAddress),
            "/api/v4/identity");

        Assert.True(result.NextInvoked);
        Assert.Equal(StatusCodes.Status204NoContent, result.StatusCode);
    }

    [Theory]
    [InlineData("192.168.10.26")]
    [InlineData("10.0.0.10")]
    public async Task Production_DeniesOtherAddressesWithoutDisclosingAddress(
        string remoteAddress)
    {
        var result = await InvokeAsync(
            ProductionOptions(),
            IPAddress.Parse(remoteAddress),
            "/api/v4/identity");

        Assert.False(result.NextInvoked);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal(AgentErrorCodes.ClientNotAllowed, ReadErrorCode(result.Body));
        Assert.DoesNotContain(remoteAddress, result.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("192.168.10.25", result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Production_DeniesRequestWithoutRemoteAddress()
    {
        var result = await InvokeAsync(
            ProductionOptions(),
            remoteAddress: null,
            "/api/v4/identity");

        Assert.False(result.NextInvoked);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal(AgentErrorCodes.ClientNotAllowed, ReadErrorCode(result.Body));
    }

    [Fact]
    public async Task Production_IgnoresForwardedForWhenConnectionAddressIsDenied()
    {
        var result = await InvokeAsync(
            ProductionOptions(),
            IPAddress.Parse("192.168.10.26"),
            "/api/v4/identity",
            forwardedFor: "192.168.10.25");

        Assert.False(result.NextInvoked);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal(AgentErrorCodes.ClientNotAllowed, ReadErrorCode(result.Body));
    }

    [Fact]
    public async Task Production_IgnoresForwardedForWhenConnectionAddressIsAllowed()
    {
        var result = await InvokeAsync(
            ProductionOptions(),
            IPAddress.Parse("192.168.10.25"),
            "/api/v4/identity",
            forwardedFor: "192.168.10.26");

        Assert.True(result.NextInvoked);
        Assert.Equal(StatusCodes.Status204NoContent, result.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1", "/health/live", true)]
    [InlineData("::1", "/health/ready", true)]
    [InlineData("127.0.0.1", "/api/v4/identity", false)]
    [InlineData("::1", "/api/v4/telnet/test", false)]
    public async Task Production_LimitsLoopbackToHealthEndpoints(
        string remoteAddress,
        string path,
        bool expectedAllowed)
    {
        var result = await InvokeAsync(
            ProductionOptions(),
            IPAddress.Parse(remoteAddress),
            path);

        Assert.Equal(expectedAllowed, result.NextInvoked);
        Assert.Equal(
            expectedAllowed
                ? StatusCodes.Status204NoContent
                : StatusCodes.Status403Forbidden,
            result.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task MockMode_AllowsLoopbackOnAllEndpoints(string remoteAddress)
    {
        var result = await InvokeAsync(
            new AgentOptions { MockMode = true },
            IPAddress.Parse(remoteAddress),
            "/api/v4/telnet/execute");

        Assert.True(result.NextInvoked);
        Assert.Equal(StatusCodes.Status204NoContent, result.StatusCode);
    }

    [Fact]
    public async Task MockMode_AllowsOptionalConfiguredViewer()
    {
        var result = await InvokeAsync(
            new AgentOptions
            {
                MockMode = true,
                AllowedViewerIpv4 = "10.20.30.40"
            },
            IPAddress.Parse("10.20.30.40"),
            "/api/v4/identity");

        Assert.True(result.NextInvoked);
        Assert.Equal(StatusCodes.Status204NoContent, result.StatusCode);
    }

    [Fact]
    public async Task MockMode_DeniesUnconfiguredNonLoopbackAddress()
    {
        var result = await InvokeAsync(
            new AgentOptions { MockMode = true },
            IPAddress.Parse("10.20.30.41"),
            "/api/v4/identity");

        Assert.False(result.NextInvoked);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.1.10")]
    public void Configuration_AcceptsExactRfc1918ViewerAddress(string viewerAddress)
    {
        var (options, folder) = NewConfiguration(viewerAddress, mockMode: false);
        try
        {
            AgentOptionsValidator.ValidateAndNormalize(options, folder);

            Assert.Equal(viewerAddress, options.AllowedViewerIpv4);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.0.2.10")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.10.20")]
    [InlineData("192.168.001.10")]
    [InlineData("192.168.1.0/24")]
    [InlineData("2001:db8::10")]
    public void Configuration_RejectsMissingOrNonPrivateProductionViewerAddress(
        string viewerAddress)
    {
        var (options, folder) = NewConfiguration(viewerAddress, mockMode: false);
        try
        {
            var exception = Assert.Throws<AgentConfigurationException>(() =>
                AgentOptionsValidator.ValidateAndNormalize(options, folder));

            Assert.Equal(AgentErrorCodes.ConfigurationInvalid, exception.Code);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Configuration_MockModeMayOmitViewerAddress()
    {
        var (options, folder) = NewConfiguration(string.Empty, mockMode: true);
        try
        {
            AgentOptionsValidator.ValidateAndNormalize(options, folder);

            Assert.Equal(string.Empty, options.AllowedViewerIpv4);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Configuration_MockModeRejectsPublicViewerAddress()
    {
        var (options, folder) = NewConfiguration("192.0.2.10", mockMode: true);
        try
        {
            var exception = Assert.Throws<AgentConfigurationException>(() =>
                AgentOptionsValidator.ValidateAndNormalize(options, folder));

            Assert.Equal(AgentErrorCodes.ConfigurationInvalid, exception.Code);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static AgentOptions ProductionOptions() => new()
    {
        AllowedViewerIpv4 = "192.168.10.25"
    };

    private static async Task<InvocationResult> InvokeAsync(
        AgentOptions options,
        IPAddress? remoteAddress,
        string path,
        string? forwardedFor = null)
    {
        var nextInvoked = false;
        var middleware = new ViewerIpAccessMiddleware(
            context =>
            {
                nextInvoked = true;
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            options);
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
        var bodyText = Encoding.UTF8.GetString(body.ToArray());
        return new InvocationResult(
            nextInvoked,
            context.Response.StatusCode,
            bodyText);
    }

    private static string ReadErrorCode(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement
            .GetProperty("error")
            .GetProperty("code")
            .GetString()!;
    }

    private static (AgentOptions Options, string Folder) NewConfiguration(
        string viewerAddress,
        bool mockMode)
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "SamsungSwitchWatch-AgentViewerIpTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return (new AgentOptions
        {
            ListenUrl = mockMode
                ? "http://127.0.0.1:0"
                : "https://0.0.0.0:18443",
            DataDirectory = folder,
            MockMode = mockMode,
            AllowedViewerIpv4 = viewerAddress,
            AllowedTargetCidrs = ["192.0.2.0/24"]
        }, folder);
    }

    private sealed record InvocationResult(
        bool NextInvoked,
        int StatusCode,
        string Body);
}
