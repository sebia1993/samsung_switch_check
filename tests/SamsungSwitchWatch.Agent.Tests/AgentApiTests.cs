using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using SamsungSwitchWatch.Agent.Api;
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
    public async Task LiveAndReadyHealthAreAnonymousAndReportSchemaReadiness()
    {
        await using var host = await TestAgentHost.StartAsync();

        using var live = await host.Client.GetAsync("/health/live");
        using var ready = await host.Client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        using var document = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        Assert.True(document.RootElement.GetProperty("ready").GetBoolean());
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
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
    public void ProductionDoesNotExposeAnonymousPairingBootstrap()
    {
        var liveOptions = new AgentOptions { MockMode = false };
        var mockOptions = new AgentOptions { MockMode = true };

        Assert.False(ApiEndpoints.ShouldMapPairingBootstrap(liveOptions, Environments.Production));
        Assert.True(ApiEndpoints.ShouldMapPairingBootstrap(mockOptions, Environments.Production));
        Assert.True(ApiEndpoints.ShouldMapPairingBootstrap(liveOptions, Environments.Development));
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
    public async Task VersionTwoFeedPagesAcknowledgementAndRecoveryWithoutGaps()
    {
        await using var host = await TestAgentHost.StartAsync();
        await host.PairAsync();

        using var down = await host.Client.PostAsync("/api/dev/simulate/TEST-SW-01/down", null);
        down.EnsureSuccessStatusCode();
        using var downJson = JsonDocument.Parse(await down.Content.ReadAsStringAsync());
        var id = downJson.RootElement.GetProperty("id").GetString();
        using var ack = await host.Client.PostAsync($"/api/v1/events/{id}/ack", null);
        ack.EnsureSuccessStatusCode();
        using var recover = await host.Client.PostAsync("/api/dev/simulate/TEST-SW-01/recover", null);
        recover.EnsureSuccessStatusCode();

        using var firstResponse = await host.Client.GetAsync("/api/v2/events/changes?after=0&limit=2");
        firstResponse.EnsureSuccessStatusCode();
        using var first = JsonDocument.Parse(await firstResponse.Content.ReadAsStringAsync());
        Assert.True(first.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal(2, first.RootElement.GetProperty("nextCursor").GetInt64());
        Assert.Equal(4, first.RootElement.GetProperty("highWatermark").GetInt64());

        using var secondResponse = await host.Client.GetAsync("/api/v2/events/changes?after=2&limit=2");
        secondResponse.EnsureSuccessStatusCode();
        using var second = JsonDocument.Parse(await secondResponse.Content.ReadAsStringAsync());
        Assert.False(second.RootElement.GetProperty("hasMore").GetBoolean());
        var changes = second.RootElement.GetProperty("changes").EnumerateArray().ToArray();
        Assert.Equal(2, changes.Length);
        Assert.Equal("Recovered", changes[0].GetProperty("changeKind").GetString());
        Assert.Equal(id, changes[0].GetProperty("event").GetProperty("id").GetString());
        Assert.True(changes[0].GetProperty("event").GetProperty("isActiveCondition").GetBoolean());
        Assert.Equal("Created", changes[1].GetProperty("changeKind").GetString());

        using var recentResponse = await host.Client.GetAsync("/api/v2/events/recent?limit=10");
        recentResponse.EnsureSuccessStatusCode();
        using var recent = JsonDocument.Parse(await recentResponse.Content.ReadAsStringAsync());
        Assert.Equal(2, recent.RootElement.GetArrayLength());

        using var snapshotResponse = await host.Client.GetAsync("/api/v2/snapshot");
        snapshotResponse.EnsureSuccessStatusCode();
        using var snapshot = JsonDocument.Parse(await snapshotResponse.Content.ReadAsStringAsync());
        Assert.Equal(4, snapshot.RootElement.GetProperty("highWatermark").GetInt64());
        Assert.Equal(0, snapshot.RootElement.GetProperty("activeCritical").GetInt64());
        Assert.True(snapshot.RootElement.GetProperty("ready").GetBoolean());
    }

    [Fact]
    public async Task VersionTwoFeedReturnsAllChangesAcrossMoreThanTwoPages()
    {
        await using var host = await TestAgentHost.StartAsync();
        await host.PairAsync();
        var store = host.Services.GetRequiredService<SqliteAgentStore>();
        const int count = 1201;
        for (var index = 0; index < count; index++)
        {
            await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Info, "bulk-v2",
                "Bulk event", "Sanitized bulk event.", EventState.New, $"bulk-v2:{index}"));
        }

        long cursor = 0;
        var received = 0;
        var pages = 0;
        do
        {
            using var response = await host.Client.GetAsync($"/api/v2/events/changes?after={cursor}&limit=500");
            response.EnsureSuccessStatusCode();
            using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var changes = page.RootElement.GetProperty("changes");
            received += changes.GetArrayLength();
            cursor = page.RootElement.GetProperty("nextCursor").GetInt64();
            pages++;
            if (!page.RootElement.GetProperty("hasMore").GetBoolean())
            {
                break;
            }
        } while (pages < 10);

        Assert.Equal(count, received);
        Assert.Equal(count, cursor);
        Assert.Equal(3, pages);
    }

    [Fact]
    public async Task VersionTwoFeedReportsRetentionGapResetContract()
    {
        await using var host = await TestAgentHost.StartAsync();
        await host.PairAsync();
        var store = host.Services.GetRequiredService<SqliteAgentStore>();
        var now = DateTimeOffset.UtcNow;
        await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Info, "expired",
            "Expired", "Expired", EventState.New, "expired", OccurredUtc: now.AddDays(-91)));
        await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Critical, "active",
            "Active", "Active", EventState.New, "active", OccurredUtc: now.AddDays(-91), IsActiveCondition: true));
        await store.InsertEventAsync(new NewEvent("TEST-SW-01", EventSeverity.Info, "recent",
            "Recent", "Recent", EventState.New, "recent", OccurredUtc: now));
        await store.RunRetentionAsync(now);

        using var response = await host.Client.GetAsync("/api/v2/events/changes?after=0&limit=500");
        response.EnsureSuccessStatusCode();
        using var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.True(page.RootElement.GetProperty("resetRequired").GetBoolean());
        Assert.Equal(3, page.RootElement.GetProperty("highWatermark").GetInt64());
        Assert.Equal(3, page.RootElement.GetProperty("resetCursor").GetInt64());
        Assert.Equal(3, page.RootElement.GetProperty("nextCursor").GetInt64());
        Assert.Empty(page.RootElement.GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public async Task ConsecutiveCollectorFailuresCreateOneEventAndNextSuccessCreatesOneRecovery()
    {
        var collector = new FailThreeThenSucceedCollector();
        await using var host = await TestAgentHost.StartAsync(collector);
        await host.PairAsync();

        using var first = await host.Client.PostAsync("/api/v1/commands/TEST-SW-01/version", null);
        using var second = await host.Client.PostAsync("/api/v1/commands/TEST-SW-01/version", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, second.StatusCode);

        using var beforeThreshold = await host.Client.GetAsync("/api/v1/events?after=0");
        beforeThreshold.EnsureSuccessStatusCode();
        using var beforeThresholdJson = JsonDocument.Parse(await beforeThreshold.Content.ReadAsStringAsync());
        Assert.Empty(beforeThresholdJson.RootElement.EnumerateArray());

        using var third = await host.Client.PostAsync("/api/v1/commands/TEST-SW-01/version", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, third.StatusCode);
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
            .Single(item => item.GetProperty("commandId").GetString() == CommandCatalog.CollectorHealthSnapshotIdFor("version"))
            .GetProperty("data");
        Assert.Equal(AgentErrorCodes.CommandTimeout, failedHealth.GetProperty("errorCode").GetString());
        Assert.True(failedHealth.TryGetProperty("lastAttemptUtc", out _));
        Assert.Equal(3, failedHealth.GetProperty("consecutiveFailures").GetInt32());
        Assert.Equal("Failed", failedHealth.GetProperty("state").GetString());

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
            .Single(item => item.GetProperty("commandId").GetString() == CommandCatalog.CollectorHealthSnapshotIdFor("version"))
            .GetProperty("data");
        Assert.Equal(JsonValueKind.Null, health.GetProperty("errorCode").ValueKind);
        Assert.True(health.TryGetProperty("lastAttemptUtc", out _));
        Assert.True(health.TryGetProperty("lastSuccessUtc", out _));
        Assert.Equal(0, health.GetProperty("consecutiveFailures").GetInt32());
        Assert.Equal("Healthy", health.GetProperty("state").GetString());
        var deviceBody = await devicesResponse.Content.ReadAsStringAsync();
        Assert.DoesNotContain("192.0.2.10", deviceBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", deviceBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw", deviceBody, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessOfAnotherCommandDoesNotClearFailedCommandHealth()
    {
        await using var host = await TestAgentHost.StartAsync(new VersionFailsSystemSucceedsCollector());
        await host.PairAsync();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var failed = await host.Client.PostAsync("/api/v1/commands/TEST-SW-01/version", null);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        }
        using var succeeded = await host.Client.PostAsync("/api/v1/commands/TEST-SW-01/system", null);
        succeeded.EnsureSuccessStatusCode();

        using var devicesResponse = await host.Client.GetAsync("/api/v1/devices");
        using var devices = JsonDocument.Parse(await devicesResponse.Content.ReadAsStringAsync());
        var collection = devices.RootElement[0].GetProperty("collection").EnumerateArray().ToArray();
        var versionHealth = collection.Single(item => item.GetProperty("commandId").GetString() ==
            CommandCatalog.CollectorHealthSnapshotIdFor("version")).GetProperty("data");
        var systemHealth = collection.Single(item => item.GetProperty("commandId").GetString() ==
            CommandCatalog.CollectorHealthSnapshotIdFor("system")).GetProperty("data");
        Assert.Equal("Failed", versionHealth.GetProperty("state").GetString());
        Assert.Equal(3, versionHealth.GetProperty("consecutiveFailures").GetInt32());
        Assert.Equal("Healthy", systemHealth.GetProperty("state").GetString());

        using var eventsResponse = await host.Client.GetAsync("/api/v1/events?after=0");
        using var events = JsonDocument.Parse(await eventsResponse.Content.ReadAsStringAsync());
        var eventItems = events.RootElement.EnumerateArray().ToArray();
        var types = eventItems.Select(item => item.GetProperty("type").GetString()).ToArray();
        Assert.Single(types);
        Assert.Equal("collector-failed", types[0]);
    }

    [Theory]
    [InlineData(AgentErrorCodes.AuthFailed)]
    [InlineData(AgentErrorCodes.CredentialCorrupt)]
    [InlineData(AgentErrorCodes.CredentialUnavailable)]
    public async Task CredentialFailureBlocksFurtherCollectionUntilCredentialFileChanges(string errorCode)
    {
        var collector = new CircuitErrorThenSuccessCollector(errorCode);
        await using var host = await TestAgentHost.StartAsync(collector);
        await host.PairAsync();

        using var first = await host.Client.PostAsync("/api/v1/commands/TEST-SW-01/version", null);
        using var blocked = await host.Client.PostAsync("/api/v1/commands/TEST-SW-01/version", null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, first.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        Assert.Equal(1, collector.Attempts);

        var options = host.Services.GetRequiredService<AgentOptions>();
        var credentialFolder = Path.Combine(options.DataDirectory, "credentials");
        Directory.CreateDirectory(credentialFolder);
        await File.WriteAllBytesAsync(Path.Combine(credentialFolder, "test-switch-readonly.bin"), "changed-test-envelope"u8.ToArray());

        using var recovered = await host.Client.PostAsync("/api/v1/commands/TEST-SW-01/version", null);
        recovered.EnsureSuccessStatusCode();
        Assert.Equal(2, collector.Attempts);

        using var eventsResponse = await host.Client.GetAsync("/api/v1/events?after=0");
        using var events = JsonDocument.Parse(await eventsResponse.Content.ReadAsStringAsync());
        var eventItems = events.RootElement.EnumerateArray().ToArray();
        var types = eventItems.Select(item => item.GetProperty("type").GetString()).ToArray();
        Assert.Collection(types,
            type => Assert.Equal("collector-auth-blocked", type),
            type => Assert.Equal("collector-auth-recovered", type));
        Assert.Equal(errorCode, eventItems[0].GetProperty("details").GetProperty("errorCode").GetString());
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 30)]
    [InlineData(3, 60)]
    [InlineData(10, 60)]
    public void PollBackoffUsesBoundedFailureSchedule(int failures, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), PollBackoffPolicy.ForFailure(failures));

    [Fact]
    public async Task LogRotationAndEmptyRefillCreateOneResetWithoutLogFlood()
    {
        var collector = new LogSequenceCollector(
            Logs("a", "b"),
            Logs("c", "d"),
            Logs(),
            Logs("e"),
            Logs("e", "f"));
        await using var host = await TestAgentHost.StartAsync(collector);
        await host.PairAsync();

        for (var poll = 0; poll < 5; poll++)
        {
            using var response = await host.Client.PostAsync("/api/v1/commands/TEST-SW-01/log_ram", null);
            response.EnsureSuccessStatusCode();
        }

        using var eventsResponse = await host.Client.GetAsync("/api/v1/events?after=0");
        using var events = JsonDocument.Parse(await eventsResponse.Content.ReadAsStringAsync());
        var eventItems = events.RootElement.EnumerateArray().ToArray();
        var types = eventItems.Select(item => item.GetProperty("type").GetString()).ToArray();
        Assert.Collection(types,
            type => Assert.Equal("log-buffer-reset", type),
            type => Assert.Equal("log-buffer-reset", type),
            type => Assert.Equal("switch-log", type));
        Assert.Equal("Info", eventItems[^1].GetProperty("severity").GetString());
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
            "Critical test event", "Sanitized critical test event.", EventState.New, "bulk:critical",
            IsActiveCondition: true));

        using var response = await host.Client.GetAsync("/api/v1/status");
        response.EnsureSuccessStatusCode();
        using var status = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(final.Sequence, status.RootElement.GetProperty("lastEventSequence").GetInt64());
        Assert.Equal(1002, status.RootElement.GetProperty("unacknowledged").GetInt64());
        Assert.Equal(1, status.RootElement.GetProperty("activeCritical").GetInt64());
    }

    private sealed class FailThreeThenSucceedCollector : IDeviceCollector
    {
        private int _attempts;

        public Task<CollectedOutput> CollectAsync(
            SwitchOptions device,
            CommandDefinition command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _attempts) <= 3)
            {
                throw new AgentOperationException(AgentErrorCodes.CommandTimeout, "Collection command timed out.", 503);
            }

            var captured = DateTimeOffset.UtcNow;
            return Task.FromResult(new CollectedOutput(device.Id, command.Id, captured,
                new JsonObject { ["model"] = "IES4224GP", ["softwareVersion"] = "TEST" },
                "Agent-only test fixture"));
        }
    }

    private sealed class VersionFailsSystemSucceedsCollector : IDeviceCollector
    {
        public Task<CollectedOutput> CollectAsync(
            SwitchOptions device,
            CommandDefinition command,
            CancellationToken cancellationToken)
        {
            if (command.Id == "version")
            {
                throw new AgentOperationException(AgentErrorCodes.CommandTimeout, "Collection command timed out.", 503);
            }
            var captured = DateTimeOffset.UtcNow;
            return Task.FromResult(new CollectedOutput(device.Id, command.Id, captured,
                new JsonObject { ["uptimeSeconds"] = 100L, ["post"] = "PASS" }, "Agent-only test fixture"));
        }
    }

    private sealed class CircuitErrorThenSuccessCollector(string errorCode) : IDeviceCollector
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task<CollectedOutput> CollectAsync(
            SwitchOptions device,
            CommandDefinition command,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                throw new AgentOperationException(errorCode, "Credential access failed.", 503);
            }
            var captured = DateTimeOffset.UtcNow;
            return Task.FromResult(new CollectedOutput(device.Id, command.Id, captured,
                new JsonObject { ["model"] = "IES4224GP", ["softwareVersion"] = "TEST" },
                "Agent-only test fixture"));
        }
    }

    private static JsonObject Logs(params string[] ids)
    {
        var entries = new JsonArray();
        foreach (var id in ids)
        {
            entries.Add(new JsonObject { ["id"] = id, ["message"] = $"Sanitized log {id}" });
        }
        return new JsonObject { ["entries"] = entries };
    }

    private sealed class LogSequenceCollector(params JsonObject[] values) : IDeviceCollector
    {
        private readonly Queue<JsonObject> _values = new(values);

        public Task<CollectedOutput> CollectAsync(
            SwitchOptions device,
            CommandDefinition command,
            CancellationToken cancellationToken)
        {
            Assert.Equal("log_ram", command.Id);
            var captured = DateTimeOffset.UtcNow;
            return Task.FromResult(new CollectedOutput(device.Id, command.Id, captured,
                _values.Dequeue(), "Sanitized log fixture"));
        }
    }
}
