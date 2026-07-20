using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Polling;
using SamsungSwitchWatch.Agent.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace SamsungSwitchWatch.Agent.Tests;

public sealed class AgentApiTests
{
    [Fact]
    public async Task HealthIsAnonymousButStatusRequiresBearerToken()
    {
        await using var host = await TestAgentHost.StartAsync();

        using var health = await host.Client.GetAsync("/health");
        using var status = await host.Client.GetAsync("/api/v1/status");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, status.StatusCode);
        var body = await status.Content.ReadAsStringAsync();
        Assert.Contains(AgentErrorCodes.AuthFailed, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairingCodeCanBeExchangedOnlyOnce()
    {
        await using var host = await TestAgentHost.StartAsync();
        using var bootstrap = await host.Client.PostAsync("/api/v1/pairing/bootstrap", null);
        bootstrap.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await bootstrap.Content.ReadAsStringAsync());
        var code = document.RootElement.GetProperty("code").GetString();

        using var first = await host.Client.PostAsJsonAsync("/api/v1/pairing/exchange", new { code });
        using var second = await host.Client.PostAsJsonAsync("/api/v1/pairing/exchange", new { code });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Contains(AgentErrorCodes.PairingInvalid, await second.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandEndpointAllowsOnlyRegisteredIdsAndNeverReturnsRawOutput()
    {
        await using var host = await TestAgentHost.StartAsync();
        await host.PairAsync();

        using var denied = await host.Client.PostAsync("/api/v1/commands/TEST-SW-01/configure", null);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Contains(AgentErrorCodes.CommandNotAllowed, await denied.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var allowed = await host.Client.PostAsync("/api/v1/commands/TEST-SW-01/version", null);
        allowed.EnsureSuccessStatusCode();
        var body = await allowed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("rawOutput", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MOCK FIXTURE", body, StringComparison.OrdinalIgnoreCase);

        using var devices = await host.Client.GetAsync("/api/v1/devices");
        var devicesBody = await devices.Content.ReadAsStringAsync();
        Assert.DoesNotContain("raw", devicesBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("192.0.2.10", devicesBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task EventsCanBeCaughtUpAcknowledgedAndRecovered()
    {
        await using var host = await TestAgentHost.StartAsync();
        await host.PairAsync();

        using var down = await host.Client.PostAsync("/api/dev/simulate/TEST-SW-01/down", null);
        down.EnsureSuccessStatusCode();
        using var downJson = JsonDocument.Parse(await down.Content.ReadAsStringAsync());
        var firstSequence = downJson.RootElement.GetProperty("sequence").GetInt64();
        var firstId = downJson.RootElement.GetProperty("id").GetString();

        using var log = await host.Client.PostAsync("/api/dev/simulate/TEST-SW-01/log", null);
        log.EnsureSuccessStatusCode();
        using var catchup = await host.Client.GetAsync($"/api/v1/events?after={firstSequence}");
        catchup.EnsureSuccessStatusCode();
        using var catchupJson = JsonDocument.Parse(await catchup.Content.ReadAsStringAsync());
        Assert.Single(catchupJson.RootElement.EnumerateArray());

        using var ack = await host.Client.PostAsync($"/api/v1/events/{firstId}/ack", null);
        ack.EnsureSuccessStatusCode();
        Assert.Contains("Acknowledged", await ack.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var recovered = await host.Client.PostAsync("/api/dev/simulate/TEST-SW-01/recover", null);
        recovered.EnsureSuccessStatusCode();
        Assert.Contains("Recovered", await recovered.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConsecutiveCollectorFailuresCreateOneEventAndNextSuccessCreatesOneRecovery()
    {
        var collector = new FailTwiceThenSucceedCollector();
        await using var host = await TestAgentHost.StartAsync(collector);
        await host.PairAsync();

        using var first = await host.Client.PostAsync("/api/v1/commands/TEST-SW-01/version", null);
        using var second = await host.Client.PostAsync("/api/v1/commands/TEST-SW-01/version", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);

        using var failures = await host.Client.GetAsync("/api/v1/events?after=0");
        failures.EnsureSuccessStatusCode();
        using var failuresJson = JsonDocument.Parse(await failures.Content.ReadAsStringAsync());
        var failureEvents = failuresJson.RootElement.EnumerateArray().ToArray();
        Assert.Single(failureEvents);
        Assert.Equal("collector-failed", failureEvents[0].GetProperty("type").GetString());
        Assert.Equal(AgentErrorCodes.CommandTimeout,
            failureEvents[0].GetProperty("details").GetProperty("errorCode").GetString());

        using var failedDevicesResponse = await host.Client.GetAsync("/api/v1/devices");
        using var failedDevices = JsonDocument.Parse(await failedDevicesResponse.Content.ReadAsStringAsync());
        var failedHealth = failedDevices.RootElement[0].GetProperty("collection").EnumerateArray()
            .Single(item => item.GetProperty("commandId").GetString() == CommandCatalog.CollectorHealthSnapshotId)
            .GetProperty("data");
        Assert.Equal(AgentErrorCodes.CommandTimeout, failedHealth.GetProperty("errorCode").GetString());
        Assert.True(failedHealth.TryGetProperty("lastAttemptUtc", out _));
        Assert.Equal(2, failedHealth.EnumerateObject().Count());

        using var recovered = await host.Client.PostAsync("/api/v1/commands/TEST-SW-01/version", null);
        recovered.EnsureSuccessStatusCode();
        using var allEventsResponse = await host.Client.GetAsync("/api/v1/events?after=0");
        using var allEvents = JsonDocument.Parse(await allEventsResponse.Content.ReadAsStringAsync());
        var types = allEvents.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("type").GetString())
            .ToArray();
        Assert.Equal(2, types.Length);
        Assert.Equal(1, types.Count(type => type == "collector-failed"));
        Assert.Equal(1, types.Count(type => type == "collector-recovered"));

        using var devicesResponse = await host.Client.GetAsync("/api/v1/devices");
        devicesResponse.EnsureSuccessStatusCode();
        using var devices = JsonDocument.Parse(await devicesResponse.Content.ReadAsStringAsync());
        var health = devices.RootElement[0].GetProperty("collection").EnumerateArray()
            .Single(item => item.GetProperty("commandId").GetString() == CommandCatalog.CollectorHealthSnapshotId)
            .GetProperty("data");
        Assert.Equal(JsonValueKind.Null, health.GetProperty("errorCode").ValueKind);
        Assert.True(health.TryGetProperty("lastAttemptUtc", out _));
        Assert.Equal(2, health.EnumerateObject().Count());
        var deviceBody = await devicesResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("192.0.2.10", deviceBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", deviceBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw", deviceBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StatusAggregatesAllEventsBeyondCatchUpPageLimit()
    {
        await using var host = await TestAgentHost.StartAsync();
        await host.PairAsync();
        var store = host.Services.GetRequiredService<SqliteAgentStore>();

        for (var index = 0; index < 1001; index++)
        {
            await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Info, "bulk-info",
                "Bulk test event", "Sanitized bulk test event.", EventState.New, $"bulk:{index}"));
        }
        var final = await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Critical, "bulk-critical",
            "Critical test event", "Sanitized critical test event.", EventState.New, "bulk:critical"));

        using var response = await host.Client.GetAsync("/api/v1/status");
        response.EnsureSuccessStatusCode();
        using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(final.Sequence, status.RootElement.GetProperty("lastEventSequence").GetInt64());
        Assert.Equal(1002, status.RootElement.GetProperty("unacknowledged").GetInt64());
        Assert.Equal(1, status.RootElement.GetProperty("activeCritical").GetInt64());
    }

    private sealed class FailTwiceThenSucceedCollector : IDeviceCollector
    {
        private int _attempts;

        public Task<CollectedOutput> CollectAsync(
            SwitchOptions device,
            CommandDefinition command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _attempts) <= 2)
            {
                throw new AgentOperationException(AgentErrorCodes.CommandTimeout, "Collection command timed out.", 503);
            }

            var captured = DateTimeOffset.UtcNow;
            return Task.FromResult(new CollectedOutput(device.Id, command.Id, captured,
                new JsonObject { ["model"] = "IES4224GP", ["softwareVersion"] = "TEST" },
                "Agent-only test fixture"));
        }
    }
}
