using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Persistence;
using SamsungSwitchWatch.Agent.Polling;
using SamsungSwitchWatch.Agent.Queries;

namespace SamsungSwitchWatch.Agent.Tests;

public sealed class ReadOnlyQueryApiTests
{
    private static readonly IReadOnlyDictionary<string, string?> Enabled =
        new Dictionary<string, string?> { ["Agent:EnableReadOnlyQueries"] = "true" };

    [Fact]
    public async Task Snapshot_AdvertisesDisabledReadOnlyQueryFeatureAndLimits()
    {
        await using var host = await TestAgentHost.StartAsync();

        using var response = await host.Client.GetAsync("/api/v3/snapshot");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var feature = payload.RootElement.GetProperty("features").GetProperty("readOnlyQueries");

        Assert.True(response.IsSuccessStatusCode);
        Assert.False(feature.GetProperty("enabled").GetBoolean());
        Assert.Equal(128, feature.GetProperty("maxCommandLength").GetInt32());
        Assert.Equal(65_536, feature.GetProperty("maxOutputBytes").GetInt32());
    }

    [Fact]
    public async Task Endpoint_IsOptInAndReturnsStableDisabledCode()
    {
        await using var host = await TestAgentHost.StartAsync();

        using var response = await PostAsync(host, "show port status");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("QUERY_DISABLED", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Endpoint_ExecutesOneNormalizedApprovedShowCommand()
    {
        var collector = new FixedQueryCollector("Port 1 Up", sessionCount: 2, reconnectCount: 1);
        await using var host = await TestAgentHost.StartAsync(
            additionalOverrides: Enabled,
            queryCollector: collector);

        using var response = await PostAsync(host, "  show   port   status  ");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(3, payload.RootElement.GetProperty("apiVersion").GetInt32());
        Assert.Equal("TEST-SW-01", payload.RootElement.GetProperty("deviceId").GetString());
        Assert.Equal("show port status", payload.RootElement.GetProperty("command").GetString());
        Assert.Equal("Port 1 Up", payload.RootElement.GetProperty("output").GetString());
        Assert.False(payload.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, payload.RootElement.GetProperty("sessionCount").GetInt32());
        Assert.Equal(1, payload.RootElement.GetProperty("reconnectCount").GetInt32());
        Assert.Equal("show port status", Assert.Single(collector.Commands));
    }

    [Theory]
    [InlineData("show running-config")]
    [InlineData("show system password")]
    [InlineData("show port status; reload")]
    [InlineData("reload")]
    public async Task Endpoint_BlocksUnsafeCommandsBeforeCollector(string command)
    {
        var collector = new FixedQueryCollector("must-not-run");
        await using var host = await TestAgentHost.StartAsync(
            additionalOverrides: Enabled,
            queryCollector: collector);

        using var response = await PostAsync(host, command);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("QUERY_COMMAND_BLOCKED", await ErrorCodeAsync(response));
        Assert.Empty(collector.Commands);
    }

    [Fact]
    public async Task Endpoint_ReturnsDeviceNotFoundWithoutCallingCollector()
    {
        var collector = new FixedQueryCollector("must-not-run");
        await using var host = await TestAgentHost.StartAsync(
            additionalOverrides: Enabled,
            queryCollector: collector);

        using var response = await PostAsync(host, "show system", "MISSING-SW");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("DEVICE_NOT_FOUND", await ErrorCodeAsync(response));
        Assert.Empty(collector.Commands);
    }

    [Fact]
    public async Task Endpoint_TruncatesAtAValidUtf8Boundary()
    {
        var overrides = new Dictionary<string, string?>(Enabled)
        {
            ["Agent:ReadOnlyQueryMaxOutputBytes"] = "1024"
        };
        var collector = new FixedQueryCollector(new string('\uac00', 500));
        await using var host = await TestAgentHost.StartAsync(
            additionalOverrides: overrides,
            queryCollector: collector);

        using var response = await PostAsync(host, "show system");
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var output = payload.RootElement.GetProperty("output").GetString()!;

        Assert.True(response.IsSuccessStatusCode);
        Assert.True(payload.RootElement.GetProperty("truncated").GetBoolean());
        Assert.InRange(Encoding.UTF8.GetByteCount(output), 1, 1024);
        Assert.All(output, character => Assert.Equal('\uac00', character));
    }

    [Fact]
    public async Task Endpoint_RateLimitsPerViewerIpWithoutQueueing()
    {
        var overrides = new Dictionary<string, string?>(Enabled)
        {
            ["Agent:ReadOnlyQueryRateLimitPerMinute"] = "1"
        };
        await using var host = await TestAgentHost.StartAsync(
            additionalOverrides: overrides,
            queryCollector: new FixedQueryCollector("ok"));

        using var first = await PostAsync(host, "show system");
        using var second = await PostAsync(host, "show version");

        Assert.True(first.IsSuccessStatusCode);
        Assert.Equal((HttpStatusCode)429, second.StatusCode);
        Assert.Equal("QUERY_RATE_LIMITED", await ErrorCodeAsync(second));
    }

    [Fact]
    public async Task Endpoint_RejectsASecondQueryWhenTheSameDeviceRemainsBusy()
    {
        var overrides = new Dictionary<string, string?>(Enabled)
        {
            ["Agent:ReadOnlyQueryDeviceWaitSeconds"] = "1",
            ["Agent:ReadOnlyQueryTotalTimeoutSeconds"] = "5"
        };
        var collector = new BlockingQueryCollector();
        await using var host = await TestAgentHost.StartAsync(
            additionalOverrides: overrides,
            queryCollector: collector);

        var firstTask = PostAsync(host, "show system");
        await collector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var second = await PostAsync(host, "show version");
        collector.Release.TrySetResult();
        using var first = await firstTask;

        Assert.True(first.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("DEVICE_BUSY", await ErrorCodeAsync(second));
    }

    [Fact]
    public async Task QuerySharesTheDeviceGateWithRegisteredChecks()
    {
        var overrides = new Dictionary<string, string?>(Enabled)
        {
            ["Agent:ReadOnlyQueryDeviceWaitSeconds"] = "1",
            ["Agent:ReadOnlyQueryTotalTimeoutSeconds"] = "5"
        };
        var registeredCollector = new BlockingDeviceCollector();
        await using var host = await TestAgentHost.StartAsync(
            collector: registeredCollector,
            additionalOverrides: overrides,
            queryCollector: new FixedQueryCollector("ok"));

        var registeredTask = host.Client.PostAsync("/api/v1/commands/TEST-SW-01/system", content: null);
        await registeredCollector.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var query = await PostAsync(host, "show system");
        registeredCollector.Release.TrySetResult();
        using var registered = await registeredTask;

        Assert.True(registered.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Conflict, query.StatusCode);
        Assert.Equal("DEVICE_BUSY", await ErrorCodeAsync(query));
    }

    [Fact]
    public async Task Endpoint_MapsTotalDeadlineToStableQueryTimeout()
    {
        var overrides = new Dictionary<string, string?>(Enabled)
        {
            ["Agent:ReadOnlyQueryTotalTimeoutSeconds"] = "1"
        };
        await using var host = await TestAgentHost.StartAsync(
            additionalOverrides: overrides,
            queryCollector: new TimeoutQueryCollector());

        using var response = await PostAsync(host, "show system");

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("QUERY_TIMEOUT", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task AuditStoresHashAndMetadataButNotTheCommandOrOutput()
    {
        const string command = "show port status";
        const string output = "SENSITIVE-SYNTHETIC-OUTPUT";
        await using var host = await TestAgentHost.StartAsync(
            additionalOverrides: Enabled,
            queryCollector: new FixedQueryCollector(output));

        using var response = await PostAsync(host, command);
        Assert.True(response.IsSuccessStatusCode);

        var counts = await host.Services.GetRequiredService<SqliteAgentStore>().GetCountsAsync();
        Assert.Equal(0, counts.RawCount);
        Assert.Equal(0, counts.EventCount);
        Assert.Equal(1, counts.AuditCount);
        using var devicesResponse = await host.Client.GetAsync("/api/v1/devices");
        using var devices = JsonDocument.Parse(await devicesResponse.Content.ReadAsStringAsync());
        Assert.Empty(devices.RootElement[0].GetProperty("collection").EnumerateArray());

        var options = host.Services.GetRequiredService<AgentOptions>();
        await using var connection = new SqliteConnection($"Data Source={options.DatabasePath}");
        await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        query.CommandText = "SELECT detail FROM audit WHERE action = 'read-only-query' ORDER BY id DESC LIMIT 1;";
        var detail = Assert.IsType<string>(await query.ExecuteScalarAsync());

        Assert.Contains("command SHA-256:", detail, StringComparison.Ordinal);
        Assert.Contains("output bytes:", detail, StringComparison.Ordinal);
        Assert.DoesNotContain(command, detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(output, detail, StringComparison.Ordinal);
    }

    private static Task<HttpResponseMessage> PostAsync(
        TestAgentHost host,
        string command,
        string deviceId = "TEST-SW-01") =>
        host.Client.PostAsJsonAsync("/api/v3/read-only-queries", new { deviceId, command });

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private sealed class FixedQueryCollector(
        string output,
        int sessionCount = 1,
        int reconnectCount = 0) : IReadOnlyQueryCollector
    {
        public List<string> Commands { get; } = [];

        public Task<ReadOnlyQueryCollectionResult> ExecuteAsync(
            SwitchOptions device,
            string command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new ReadOnlyQueryCollectionResult(
                output, now, now, sessionCount, reconnectCount));
        }
    }

    private sealed class BlockingQueryCollector : IReadOnlyQueryCollector
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ReadOnlyQueryCollectionResult> ExecuteAsync(
            SwitchOptions device,
            string command,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            return new ReadOnlyQueryCollectionResult("ok", now, now, 1, 0);
        }
    }

    private sealed class TimeoutQueryCollector : IReadOnlyQueryCollector
    {
        public async Task<ReadOnlyQueryCollectionResult> ExecuteAsync(
            SwitchOptions device,
            string command,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class BlockingDeviceCollector : IDeviceCollector
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CollectedOutput> CollectAsync(
            SwitchOptions device,
            CommandDefinition command,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new CollectedOutput(
                device.Id,
                command.Id,
                DateTimeOffset.UtcNow,
                new JsonObject { ["uptimeSeconds"] = 100L, ["post"] = "PASS" },
                "synthetic raw");
        }
    }
}
