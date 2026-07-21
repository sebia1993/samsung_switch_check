using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Persistence;
using SamsungSwitchWatch.Agent.Polling;
using SamsungSwitchWatch.Agent.Security;
using SamsungSwitchWatch.Core.Profiles;
using System.Globalization;

namespace SamsungSwitchWatch.Agent.Api;

public sealed class AgentReadinessService(
    AgentOptions options,
    SqliteAgentStore store,
    CertificateStatusService certificates,
    ICredentialVault credentials,
    AgentRuntimeState runtime,
    ILogger<AgentReadinessService> logger)
{
    private static readonly HashSet<string> RequiredCommandIds =
        [CommandIds.LogRam, CommandIds.InterfaceStatus];

    public async Task<AgentReadiness> CheckAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var storage = await store.CheckReadinessAsync(cancellationToken);
        if (!storage.Ready)
        {
            return NotReady(storage.ErrorCode ?? AgentErrorCodes.StorageWriteFailed, storage.SchemaVersion, now);
        }

        if (options.Https.Enabled &&
            (!certificates.Status.HttpsEnabled ||
             string.Equals(certificates.Status.State, "unavailable", StringComparison.Ordinal)))
        {
            return NotReady(AgentErrorCodes.CertificateUnavailable, storage.SchemaVersion, now);
        }
        if (options.Https.Enabled &&
            string.Equals(certificates.Status.State, "expired", StringComparison.Ordinal))
        {
            return NotReady(AgentErrorCodes.CertificateExpired, storage.SchemaVersion, now);
        }

        if (!options.MockMode)
        {
            try
            {
                foreach (var device in options.Switches)
                {
                    if (await credentials.GetAsync(device.CredentialId, cancellationToken) is null)
                    {
                        return NotReady(AgentErrorCodes.CredentialUnavailable, storage.SchemaVersion, now);
                    }
                }
            }
            catch (AgentOperationException ex)
            {
                logger.LogWarning("Agent credential readiness check failed with {ErrorCode}.", ex.Code);
                return NotReady(ex.Code, storage.SchemaVersion, now);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                logger.LogWarning("Agent credential readiness check failed with {ErrorCode}.",
                    AgentErrorCodes.CredentialUnavailable);
                return NotReady(AgentErrorCodes.CredentialUnavailable, storage.SchemaVersion, now);
            }
        }

        if (options.EnablePolling)
        {
            var heartbeat = runtime.SchedulerHeartbeatUtc;
            var staleAfter = TimeSpan.FromSeconds(Math.Max(120, options.SchedulerTickSeconds * 3));
            if (!heartbeat.HasValue || now - heartbeat.Value > staleAfter)
            {
                return NotReady("SCHEDULER_STALE", storage.SchemaVersion, now);
            }
        }

        if (!options.MockMode && options.EnablePolling)
        {
            var snapshots = await store.GetAllSnapshotsAsync(cancellationToken);
            foreach (var device in options.Switches)
            {
                var authCircuit = snapshots.FirstOrDefault(snapshot =>
                    string.Equals(snapshot.DeviceId, device.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(snapshot.CommandId, CommandCatalog.CollectorAuthCircuitSnapshotId,
                        StringComparison.OrdinalIgnoreCase));
                if (authCircuit?.Data["blocked"]?.GetValue<bool>() == true)
                {
                    var code = authCircuit.Data["errorCode"]?.GetValue<string>();
                    return NotReady(string.IsNullOrWhiteSpace(code) ? AgentErrorCodes.AuthFailed : code,
                        storage.SchemaVersion, now);
                }

                foreach (var command in CommandCatalog.Registered.Values)
                {
                    var healthId = CommandCatalog.CollectorHealthSnapshotIdFor(command.Id);
                    var health = snapshots.FirstOrDefault(snapshot =>
                        string.Equals(snapshot.DeviceId, device.Id, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(snapshot.CommandId, healthId, StringComparison.OrdinalIgnoreCase));
                    var state = health?.Data["state"]?.GetValue<string>();
                    var errorCode = health?.Data["errorCode"]?.GetValue<string>();
                    if (state is "Failed" or "AuthBlocked")
                    {
                        return NotReady(string.IsNullOrWhiteSpace(errorCode)
                            ? AgentErrorCodes.PromptParseFailed
                            : errorCode, storage.SchemaVersion, now);
                    }
                    if (string.Equals(state, "Unsupported", StringComparison.Ordinal))
                    {
                        if (RequiredCommandIds.Contains(command.Id))
                        {
                            return NotReady(AgentErrorCodes.CollectorUnusable, storage.SchemaVersion, now);
                        }
                        // A model or firmware may not implement every optional read-only command.
                        // Other collectors remain usable, so readiness is intentionally partial-ready.
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(health?.Data["lastSuccessUtc"]?.GetValue<string>()))
                    {
                        return NotReady(AgentErrorCodes.CollectorInitializing, storage.SchemaVersion, now);
                    }

                    var lastAttemptText = health?.Data["lastAttemptUtc"]?.GetValue<string>();
                    if (!DateTimeOffset.TryParse(lastAttemptText, CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal, out var lastAttempt) ||
                        now - lastAttempt > FreshnessWindow(command))
                    {
                        return NotReady(AgentErrorCodes.CollectorStale, storage.SchemaVersion, now);
                    }
                }
            }
        }

        return new AgentReadiness(true, "ready", null, storage.SchemaVersion,
            runtime.SchedulerHeartbeatUtc, now);
    }

    private AgentReadiness NotReady(string code, int schemaVersion, DateTimeOffset checkedUtc) =>
        new(false, "not-ready", code, schemaVersion, runtime.SchedulerHeartbeatUtc, checkedUtc);

    private TimeSpan FreshnessWindow(CommandDefinition command)
    {
        var schedulerGrace = TimeSpan.FromSeconds(Math.Max(30, options.SchedulerTickSeconds * 3));
        return command.Interval + schedulerGrace;
    }
}

public sealed class StorageIntegrityService(
    SqliteAgentStore store,
    ILogger<StorageIntegrityService> logger) : BackgroundService
{
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
                if (!await store.RefreshIntegrityStatusAsync(stoppingToken))
                {
                    logger.LogWarning("Agent storage periodic integrity status is not ready.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Agent storage periodic integrity check could not complete.");
            }
        }
    }
}
