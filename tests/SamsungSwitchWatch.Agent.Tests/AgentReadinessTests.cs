using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SamsungSwitchWatch.Agent.Api;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Persistence;
using SamsungSwitchWatch.Agent.Polling;
using SamsungSwitchWatch.Agent.Security;

namespace SamsungSwitchWatch.Agent.Tests;

public sealed class AgentReadinessTests
{
    [Fact]
    public async Task LiveReadinessRequiresAttemptsButAllowsUnsupportedOptionalCommand()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-ReadinessTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var options = new AgentOptions
            {
                DataDirectory = folder,
                MockMode = false,
                EnablePolling = true,
                Switches = [new SwitchOptions()]
            };
            var store = new SqliteAgentStore(options, NullLogger<SqliteAgentStore>.Instance);
            await store.InitializeAsync();
            var runtime = new AgentRuntimeState();
            runtime.TouchScheduler(DateTimeOffset.UtcNow);
            var readiness = new AgentReadinessService(options, store, new AvailableCredentialVault(),
                runtime, NullLogger<AgentReadinessService>.Instance);

            var initializing = await readiness.CheckAsync();
            Assert.False(initializing.Ready);
            Assert.Equal(AgentErrorCodes.CollectorInitializing, initializing.Code);

            var now = DateTimeOffset.UtcNow;
            foreach (var command in CommandCatalog.Registered.Values)
            {
                var unsupported = command.Id == "system";
                await store.UpsertSnapshotAsync(new DeviceSnapshot("TEST-SW-01",
                    CommandCatalog.CollectorHealthSnapshotIdFor(command.Id), now, new JsonObject
                    {
                        ["errorCode"] = unsupported ? AgentErrorCodes.ParserUnsupported : null,
                        ["lastAttemptUtc"] = now.ToString("O"),
                        ["lastSuccessUtc"] = unsupported ? null : now.ToString("O"),
                        ["consecutiveFailures"] = unsupported ? 1 : 0,
                        ["state"] = unsupported ? "Unsupported" : "Healthy"
                    }));
            }

            var partialReady = await readiness.CheckAsync();
            Assert.True(partialReady.Ready);

            var staleAttempt = now.AddHours(-2);
            await store.UpsertSnapshotAsync(new DeviceSnapshot("TEST-SW-01",
                CommandCatalog.CollectorHealthSnapshotIdFor("version"), staleAttempt, new JsonObject
                {
                    ["errorCode"] = null,
                    ["lastAttemptUtc"] = staleAttempt.ToString("O"),
                    ["lastSuccessUtc"] = staleAttempt.ToString("O"),
                    ["consecutiveFailures"] = 0,
                    ["state"] = "Healthy"
                }));
            var stale = await readiness.CheckAsync();
            Assert.False(stale.Ready);
            Assert.Equal(AgentErrorCodes.CollectorStale, stale.Code);
            await store.UpsertSnapshotAsync(new DeviceSnapshot("TEST-SW-01",
                CommandCatalog.CollectorHealthSnapshotIdFor("version"), now, new JsonObject
                {
                    ["errorCode"] = null,
                    ["lastAttemptUtc"] = now.ToString("O"),
                    ["lastSuccessUtc"] = now.ToString("O"),
                    ["consecutiveFailures"] = 0,
                    ["state"] = "Healthy"
                }));

            await store.UpsertSnapshotAsync(new DeviceSnapshot("TEST-SW-01",
                CommandCatalog.CollectorHealthSnapshotIdFor("log_ram"), now, new JsonObject
                {
                    ["errorCode"] = AgentErrorCodes.ParserUnsupported,
                    ["lastAttemptUtc"] = now.ToString("O"),
                    ["lastSuccessUtc"] = null,
                    ["consecutiveFailures"] = 1,
                    ["state"] = "Unsupported"
                }));
            var coreCollectorUnsupported = await readiness.CheckAsync();
            Assert.False(coreCollectorUnsupported.Ready);
            Assert.Equal(AgentErrorCodes.CollectorUnusable, coreCollectorUnsupported.Code);

            await store.UpsertSnapshotAsync(new DeviceSnapshot("TEST-SW-01",
                CommandCatalog.CollectorHealthSnapshotIdFor("log_ram"), now, new JsonObject
                {
                    ["errorCode"] = null,
                    ["lastAttemptUtc"] = now.ToString("O"),
                    ["lastSuccessUtc"] = now.ToString("O"),
                    ["consecutiveFailures"] = 0,
                    ["state"] = "Healthy"
                }));

            await store.UpsertSnapshotAsync(new DeviceSnapshot("TEST-SW-01",
                CommandCatalog.CollectorHealthSnapshotIdFor("version"), now, new JsonObject
                {
                    ["errorCode"] = AgentErrorCodes.IncompleteOutput,
                    ["lastAttemptUtc"] = now.ToString("O"),
                    ["lastSuccessUtc"] = now.AddMinutes(-1).ToString("O"),
                    ["consecutiveFailures"] = 3,
                    ["state"] = "Failed"
                }));
            var failed = await readiness.CheckAsync();
            Assert.False(failed.Ready);
            Assert.Equal(AgentErrorCodes.IncompleteOutput, failed.Code);

            await store.UpsertSnapshotAsync(new DeviceSnapshot("TEST-SW-01",
                CommandCatalog.CollectorAuthCircuitSnapshotId, now, new JsonObject
                {
                    ["blocked"] = true,
                    ["errorCode"] = AgentErrorCodes.CredentialCorrupt
                }));
            var authBlocked = await readiness.CheckAsync();
            Assert.False(authBlocked.Ready);
            Assert.Equal(AgentErrorCodes.CredentialCorrupt, authBlocked.Code);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
                // Best-effort temporary cleanup on Windows.
            }
        }
    }

    private sealed class AvailableCredentialVault : ICredentialVault
    {
        public Task StoreAsync(string credentialId, SwitchCredential credential,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<SwitchCredential?> GetAsync(string credentialId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SwitchCredential?>(new SwitchCredential("readonly", "test-only"));
    }
}
