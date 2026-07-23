using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SamsungSwitchWatch.Agent.Execution;
using SamsungSwitchWatch.Core.Diagnostics;
using SamsungSwitchWatch.Core.Telnet;

namespace SamsungSwitchWatch.Agent.Tests;

public sealed class StatelessTelnetApiTests
{
    [Fact]
    public async Task Runtime_ExposesOnlyStatelessV4SurfaceAndNoBackgroundServices()
    {
        await using var host = await TestAgentHost.StartAsync();

        using var identityResponse = await host.Client.GetAsync("/api/v4/identity");
        using var liveResponse = await host.Client.GetAsync("/health/live");
        using var oldStatus = await host.Client.GetAsync("/api/v1/status");
        using var identity = JsonDocument.Parse(
            await identityResponse.Content.ReadAsStringAsync());

        Assert.True(identityResponse.IsSuccessStatusCode);
        Assert.True(liveResponse.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, oldStatus.StatusCode);
        Assert.Equal(4, identity.RootElement.GetProperty("apiVersion").GetInt32());
        Assert.Equal("https", identity.RootElement.GetProperty("protocol").GetString());
        Assert.Matches(
            "^[0-9A-F]{64}$",
            identity.RootElement.GetProperty("certificatePublicKeySha256").GetString()!);
        Assert.DoesNotContain(
            host.Services.GetServices<IHostedService>(),
            service => service.GetType().Namespace?.StartsWith(
                "SamsungSwitchWatch.Agent",
                StringComparison.Ordinal) == true);
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.DataDirectory));
    }

    [Fact]
    public async Task Test_ConnectsWithoutExecutingACommand()
    {
        var executor = new RecordingExecutor();
        await using var host = await TestAgentHost.StartAsync(executor);

        using var response = await PostTestAsync(host, new
        {
            requestId = "test-1",
            host = "192.0.2.10",
            port = 23,
            model = "IES4224GP",
            username = "operator",
            password = "login-secret",
            enablePassword = "enable-secret",
            purpose = "test"
        });
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(response.IsSuccessStatusCode);
        var request = Assert.Single(executor.Requests);
        Assert.Empty(request.Commands);
        Assert.Equal("login-secret", request.Credentials.Password);
        Assert.Equal("enable-secret", request.Credentials.EnablePassword);
        Assert.Empty(body.RootElement.GetProperty("commands").EnumerateArray());
    }

    [Fact]
    public async Task Execute_NormalizesAndReturnsAllOneLineShowCommands()
    {
        var executor = new RecordingExecutor();
        await using var host = await TestAgentHost.StartAsync(executor);

        using var response = await PostExecuteAsync(host, new
        {
            requestId = "manual-1",
            host = "192.0.2.10",
            port = 23,
            model = "IES4226XP",
            username = "operator",
            password = "login-secret",
            purpose = "manual",
            commands = new[]
            {
                "  show   port status  ",
                "show running-config"
            }
        });
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(
            ["show port status", "show running-config"],
            Assert.Single(executor.Requests).Commands);
        Assert.Equal(2, body.RootElement.GetProperty("commands").GetArrayLength());
        Assert.Equal(1, body.RootElement.GetProperty("sessionCount").GetInt32());
        Assert.Equal(0, body.RootElement.GetProperty("reconnectCount").GetInt32());
        Assert.DoesNotContain("login-secret", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("reload")]
    [InlineData("show port status; reload")]
    [InlineData("show port status | include Up")]
    [InlineData("show port status\nreload")]
    public async Task Execute_BlocksNonShowAndCommandInjection(string command)
    {
        var executor = new RecordingExecutor();
        await using var host = await TestAgentHost.StartAsync(executor);

        using var response = await PostExecuteAsync(host, ValidExecute(command));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("QUERY_COMMAND_BLOCKED", await ErrorCodeAsync(response));
        Assert.Empty(executor.Requests);
    }

    [Theory]
    [InlineData("192.0.3.10", 23)]
    [InlineData("2001:db8::10", 23)]
    [InlineData("192.0.2.10", 2323)]
    [InlineData("0300.0.2.10", 23)]
    public async Task Execute_RejectsTargetsOutsideConfiguredExactIpv4Port23(
        string address,
        int port)
    {
        var executor = new RecordingExecutor();
        await using var host = await TestAgentHost.StartAsync(executor);

        using var response = await PostExecuteAsync(host, new
        {
            requestId = "manual-1",
            host = address,
            port,
            model = "IES4224GP",
            username = "operator",
            password = "login-secret",
            purpose = "manual",
            commands = new[] { "show system" }
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("TARGET_NOT_ALLOWED", await ErrorCodeAsync(response));
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Execute_RejectsUnknownFieldsAndNeverInvokesExecutor()
    {
        var executor = new RecordingExecutor();
        await using var host = await TestAgentHost.StartAsync(executor);

        using var response = await PostExecuteAsync(host, new
        {
            requestId = "manual-1",
            host = "192.0.2.10",
            port = 23,
            model = "IES4224GP",
            username = "operator",
            password = "login-secret",
            purpose = "manual",
            commands = new[] { "show system" },
            configure = "terminal"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("REQUEST_INVALID", await ErrorCodeAsync(response));
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Execute_RejectsOversizedJsonBeforeBindingOrExecution()
    {
        var executor = new RecordingExecutor();
        await using var host = await TestAgentHost.StartAsync(executor);
        var oversized = new
        {
            requestId = "manual-1",
            host = "192.0.2.10",
            port = 23,
            model = "IES4224GP",
            username = "operator",
            password = new string('p', 40_000),
            purpose = "manual",
            commands = new[] { "show system" }
        };

        using var response = await PostExecuteAsync(host, oversized);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal("REQUEST_TOO_LARGE", await ErrorCodeAsync(response));
        Assert.Empty(executor.Requests);
    }

    [Fact]
    public async Task Execute_MapsCoreFailureWithoutLeakingRequestValues()
    {
        await using var host = await TestAgentHost.StartAsync(
            new FailingExecutor(new SwitchWatchException(
                new DiagnosticError(
                    ErrorCodes.EnableFailed,
                    "enable",
                    "The switch rejected privilege elevation."))));

        using var response = await PostExecuteAsync(host, ValidExecute("show system"));
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("ENABLE_FAILED", await ErrorCodeAsync(response));
        Assert.DoesNotContain("192.0.2.10", body, StringComparison.Ordinal);
        Assert.DoesNotContain("login-secret", body, StringComparison.Ordinal);
        Assert.DoesNotContain("show system", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Execute_RateLimitsPerClientWithoutPersistingRequests()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Agent:RateLimitPerMinute"] = "1"
        };
        await using var host = await TestAgentHost.StartAsync(
            new RecordingExecutor(),
            overrides);

        using var first = await PostExecuteAsync(host, ValidExecute("show system"));
        using var second = await PostExecuteAsync(host, ValidExecute("show version"));

        Assert.True(first.IsSuccessStatusCode);
        Assert.Equal((HttpStatusCode)429, second.StatusCode);
        Assert.Equal("QUERY_RATE_LIMITED", await ErrorCodeAsync(second));
        Assert.Empty(Directory.EnumerateFileSystemEntries(host.DataDirectory));
    }

    private static object ValidExecute(string command) => new
    {
        requestId = "manual-1",
        host = "192.0.2.10",
        port = 23,
        model = "IES4224GP",
        username = "operator",
        password = "login-secret",
        purpose = "manual",
        commands = new[] { command }
    };

    private static Task<HttpResponseMessage> PostTestAsync(
        TestAgentHost host,
        object request) =>
        host.Client.PostAsJsonAsync("/api/v4/telnet/test", request);

    private static Task<HttpResponseMessage> PostExecuteAsync(
        TestAgentHost host,
        object request) =>
        host.Client.PostAsJsonAsync("/api/v4/telnet/execute", request);

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private sealed class RecordingExecutor : IStatelessTelnetExecutor
    {
        public List<StatelessTelnetRequest> Requests { get; } = [];

        public Task<TelnetApiResult> ExecuteAsync(
            StatelessTelnetRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var now = DateTimeOffset.UtcNow;
            var outputs = request.Commands.Select(command =>
                new TelnetApiCommandResult(command, "synthetic output", false, now)).ToArray();
            return Task.FromResult(new TelnetApiResult(
                4,
                request.RequestId,
                true,
                request.Credentials.EnablePassword is null ? "user" : "privileged",
                request.Credentials.EnablePassword is null ? ">" : "#",
                now,
                now,
                0,
                1,
                0,
                outputs));
        }
    }

    private sealed class FailingExecutor(SwitchWatchException exception)
        : IStatelessTelnetExecutor
    {
        public Task<TelnetApiResult> ExecuteAsync(
            StatelessTelnetRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<TelnetApiResult>(exception);
    }
}
