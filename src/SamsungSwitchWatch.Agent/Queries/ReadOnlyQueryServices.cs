using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Agent.Persistence;
using SamsungSwitchWatch.Agent.Polling;
using SamsungSwitchWatch.Agent.Security;
using SamsungSwitchWatch.Core.Diagnostics;
using SamsungSwitchWatch.Core.Profiles;
using SamsungSwitchWatch.Core.Telnet;

namespace SamsungSwitchWatch.Agent.Queries;

public sealed record ReadOnlyQueryCollectionResult(
    string Output,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    int SessionCount,
    int ReconnectCount);

public sealed record ReadOnlyQueryExecutionResult(
    int ApiVersion,
    string DeviceId,
    string Command,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    long ElapsedMs,
    string Output,
    bool Truncated,
    int SessionCount,
    int ReconnectCount);

public interface IReadOnlyQueryCollector
{
    Task<ReadOnlyQueryCollectionResult> ExecuteAsync(
        SwitchOptions device,
        string command,
        CancellationToken cancellationToken = default);
}

public sealed class CoreTelnetReadOnlyQueryCollector(
    ITelnetClient telnet,
    DeviceProfileRegistry profiles,
    ICredentialVault credentials) : IReadOnlyQueryCollector
{
    private const string QueryCommandId = "read_only_query";

    public async Task<ReadOnlyQueryCollectionResult> ExecuteAsync(
        SwitchOptions device,
        string command,
        CancellationToken cancellationToken = default)
    {
        var validation = ReadOnlyQueryPolicy.Validate(command);
        if (!validation.IsAllowed || validation.NormalizedCommand is null)
        {
            throw new AgentOperationException(AgentErrorCodes.QueryCommandBlocked,
                "Only approved read-only show commands are allowed.", 400);
        }

        if (!profiles.TryGet(device.Model, out var registeredProfile))
        {
            throw new AgentOperationException(AgentErrorCodes.ParserUnsupported,
                "The switch model does not have a Telnet prompt profile.", 503);
        }

        // Credential access happens only after the authoritative query policy has
        // accepted the command.
        var credential = await credentials.GetAsync(device.CredentialId, cancellationToken)
            ?? throw new AgentOperationException(AgentErrorCodes.CredentialUnavailable,
                "Switch credential is not configured on the Agent.", 503);
        var queryProfile = new DeviceCommandProfile(
            registeredProfile.Model,
            registeredProfile.Telnet,
            [new ReadOnlyCommandDefinition(
                QueryCommandId,
                "Read-only query",
                validation.NormalizedCommand,
                ReadOnlyQueryPolicy.CommandTimeout,
                60)]);

        try
        {
            var result = await telnet.ExecuteRegisteredAsync(
                new TelnetEndpoint(device.Host, device.Port),
                new TelnetCredentials(credential.Username, credential.Password),
                queryProfile,
                [QueryCommandId],
                cancellationToken);
            var output = result.Outputs.Single();
            return new ReadOnlyQueryCollectionResult(
                output.NormalizedOutput,
                result.StartedAt,
                result.CompletedAt,
                result.SessionCount,
                result.ReconnectCount);
        }
        catch (SwitchWatchException exception)
        {
            throw new AgentOperationException(
                NormalizeTelnetCode(exception.Error.Code),
                exception.Error.Message,
                exception.Error.IsRetryable ? 503 : 400);
        }
    }

    private static string NormalizeTelnetCode(string code) => code switch
    {
        AgentErrorCodes.TcpTimeout or
        AgentErrorCodes.TelnetNegotiationFailed or
        AgentErrorCodes.LoginPromptNotFound or
        AgentErrorCodes.AuthFailed or
        AgentErrorCodes.CommandTimeout or
        AgentErrorCodes.PromptParseFailed or
        AgentErrorCodes.TelnetSessionClosed or
        AgentErrorCodes.OutputLimitExceeded or
        AgentErrorCodes.Ipv6Unsupported => code,
        _ => AgentErrorCodes.PromptParseFailed
    };
}

public sealed class MockReadOnlyQueryCollector : IReadOnlyQueryCollector
{
    public Task<ReadOnlyQueryCollectionResult> ExecuteAsync(
        SwitchOptions device,
        string command,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = DateTimeOffset.UtcNow;
        var output = command.StartsWith("show port", StringComparison.OrdinalIgnoreCase) ||
                     command.StartsWith("show interface", StringComparison.OrdinalIgnoreCase)
            ? "Port       Admin  Link  Speed\r\n1          Up     Up    1000M\r\n24         Up     Up    1000M"
            : command.Contains("log", StringComparison.OrdinalIgnoreCase) ||
              command.Contains("sylog", StringComparison.OrdinalIgnoreCase)
                ? "2026-01-01 00:00:00 INFO Mock baseline log entry."
                : "Mock read-only query completed successfully.";
        var completed = DateTimeOffset.UtcNow;
        return Task.FromResult(new ReadOnlyQueryCollectionResult(output, started, completed, 1, 0));
    }
}

public sealed class ReadOnlyQueryRateLimiter(AgentOptions options)
{
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _windows =
        new(StringComparer.OrdinalIgnoreCase);

    public bool TryConsume(string viewerIp, DateTimeOffset now)
    {
        var window = _windows.GetOrAdd(viewerIp, static _ => new Queue<DateTimeOffset>());
        lock (window)
        {
            var cutoff = now.AddMinutes(-1);
            while (window.TryPeek(out var occurred) && occurred <= cutoff)
            {
                window.Dequeue();
            }

            if (window.Count >= options.ReadOnlyQueryRateLimitPerMinute)
            {
                return false;
            }

            window.Enqueue(now);
            return true;
        }
    }
}

public sealed class ReadOnlyQueryExecutionService(
    AgentOptions options,
    IReadOnlyQueryCollector collector,
    DeviceExecutionGateRegistry executionGates,
    ReadOnlyQueryRateLimiter rateLimiter,
    SqliteAgentStore store,
    ILogger<ReadOnlyQueryExecutionService> logger)
{
    public async Task<ReadOnlyQueryExecutionResult> ExecuteAsync(
        string deviceId,
        string command,
        string viewerIp,
        CancellationToken cancellationToken = default)
    {
        if (!options.EnableReadOnlyQueries)
        {
            throw new AgentOperationException(AgentErrorCodes.QueryDisabled,
                "Read-only Viewer queries are disabled on this Agent.", 403);
        }

        if (!rateLimiter.TryConsume(viewerIp, DateTimeOffset.UtcNow))
        {
            await WriteAuditBestEffortAsync(deviceId, viewerIp, command, "denied",
                AgentErrorCodes.QueryRateLimited, 0, 0, false, 0, 0, cancellationToken);
            throw new AgentOperationException(AgentErrorCodes.QueryRateLimited,
                "The Viewer query rate limit was reached.", 429);
        }

        var validation = ReadOnlyQueryPolicy.Validate(command, options.ReadOnlyQueryMaxCommandLength);
        if (!validation.IsAllowed || validation.NormalizedCommand is null)
        {
            await WriteAuditBestEffortAsync(deviceId, viewerIp, command, "denied",
                AgentErrorCodes.QueryCommandBlocked, 0, 0, false, 0, 0, cancellationToken);
            throw new AgentOperationException(AgentErrorCodes.QueryCommandBlocked,
                "Only approved read-only show commands are allowed.", 400);
        }
        var normalizedCommand = validation.NormalizedCommand;

        var device = options.Switches.SingleOrDefault(item =>
            string.Equals(item.Id, deviceId, StringComparison.OrdinalIgnoreCase));
        if (device is null)
        {
            await WriteAuditBestEffortAsync(deviceId, viewerIp, normalizedCommand, "failed",
                AgentErrorCodes.DeviceNotFound, 0, 0, false, 0, 0, cancellationToken);
            throw new AgentOperationException(AgentErrorCodes.DeviceNotFound, "Device was not found.", 404);
        }

        var startedUtc = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.StartNew();
        using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalTimeout.CancelAfter(TimeSpan.FromSeconds(options.ReadOnlyQueryTotalTimeoutSeconds));
        DeviceExecutionLease? lease = null;
        try
        {
            lease = await executionGates.TryAcquireAsync(
                device.Id,
                TimeSpan.FromSeconds(options.ReadOnlyQueryDeviceWaitSeconds),
                totalTimeout.Token);
            if (lease is null)
            {
                throw new AgentOperationException(AgentErrorCodes.DeviceBusy,
                    "The device is busy with another check. Try again shortly.", 409);
            }

            var collected = await collector.ExecuteAsync(device, normalizedCommand, totalTimeout.Token);
            var (output, truncated, outputBytes) = TruncateUtf8(
                collected.Output,
                options.ReadOnlyQueryMaxOutputBytes);
            elapsed.Stop();
            var completedUtc = DateTimeOffset.UtcNow;
            await WriteAuditBestEffortAsync(device.Id, viewerIp, normalizedCommand, "success", null,
                elapsed.ElapsedMilliseconds, outputBytes, truncated, collected.SessionCount,
                collected.ReconnectCount, cancellationToken);
            return new ReadOnlyQueryExecutionResult(
                3,
                device.Id,
                normalizedCommand,
                startedUtc,
                completedUtc,
                elapsed.ElapsedMilliseconds,
                output,
                truncated,
                collected.SessionCount,
                collected.ReconnectCount);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && totalTimeout.IsCancellationRequested)
        {
            elapsed.Stop();
            await WriteAuditBestEffortAsync(device.Id, viewerIp, normalizedCommand, "failed",
                AgentErrorCodes.QueryTimeout, elapsed.ElapsedMilliseconds, 0, false, 0, 0,
                cancellationToken);
            throw new AgentOperationException(AgentErrorCodes.QueryTimeout,
                "The read-only query exceeded its total time limit.", 504);
        }
        catch (AgentOperationException exception)
        {
            elapsed.Stop();
            var code = string.Equals(exception.Code, AgentErrorCodes.CommandTimeout, StringComparison.Ordinal)
                ? AgentErrorCodes.QueryTimeout
                : exception.Code;
            await WriteAuditBestEffortAsync(device.Id, viewerIp, normalizedCommand, "failed", code,
                elapsed.ElapsedMilliseconds, 0, false, 0, 0, cancellationToken);
            if (!string.Equals(code, exception.Code, StringComparison.Ordinal))
            {
                throw new AgentOperationException(code, "The read-only query timed out.", 504);
            }
            throw;
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }
        }
    }

    private async Task WriteAuditBestEffortAsync(
        string? deviceId,
        string viewerIp,
        string command,
        string outcome,
        string? errorCode,
        long elapsedMs,
        int outputBytes,
        bool truncated,
        int sessionCount,
        int reconnectCount,
        CancellationToken cancellationToken)
    {
        var commandHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(command)));
        var detail = $"Viewer IP: {viewerIp}; command SHA-256: {commandHash}; elapsed ms: {elapsedMs}; " +
                     $"error code: {errorCode ?? "none"}; output bytes: {outputBytes}; truncated: {truncated}; " +
                     $"sessions: {sessionCount}; reconnects: {reconnectCount}.";
        try
        {
            await store.InsertAuditAsync(new AuditEntry(
                DateTimeOffset.UtcNow,
                "read-only-query",
                "viewer-http",
                deviceId,
                outcome,
                detail),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(
                "Read-only query audit could not be stored for {DeviceId}; result {Outcome}; error type {ErrorType}.",
                deviceId, outcome, exception.GetType().Name);
        }
    }

    internal static (string Output, bool Truncated, int OutputBytes) TruncateUtf8(string output, int maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (maximumBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var totalBytes = Encoding.UTF8.GetByteCount(output);
        if (totalBytes <= maximumBytes)
        {
            return (output, false, totalBytes);
        }

        var characterCount = 0;
        var byteCount = 0;
        foreach (var rune in output.EnumerateRunes())
        {
            if (byteCount + rune.Utf8SequenceLength > maximumBytes)
            {
                break;
            }
            byteCount += rune.Utf8SequenceLength;
            characterCount += rune.Utf16SequenceLength;
        }

        return (output[..characterCount], true, byteCount);
    }
}
