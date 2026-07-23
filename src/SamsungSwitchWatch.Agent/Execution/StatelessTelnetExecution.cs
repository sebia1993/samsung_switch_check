using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Domain;
using SamsungSwitchWatch.Core.Diagnostics;
using SamsungSwitchWatch.Core.Profiles;
using SamsungSwitchWatch.Core.Telnet;

namespace SamsungSwitchWatch.Agent.Execution;

public sealed class TelnetApiRequest
{
    public string? RequestId { get; init; }
    public string? Host { get; init; }
    public int Port { get; init; } = 23;
    public string? Model { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? EnablePassword { get; init; }
    public string? Purpose { get; init; }
    public IReadOnlyList<string>? Commands { get; init; }

    public override string ToString() =>
        "TelnetApiRequest { Endpoint = [REDACTED], Credentials = [REDACTED], Commands = [REDACTED] }";
}

public sealed record TelnetApiCommandResult(
    string Command,
    string Output,
    bool Truncated,
    DateTimeOffset CollectedUtc)
{
    public override string ToString() =>
        "TelnetApiCommandResult { Command = [REDACTED], Output = [REDACTED] }";
}

public sealed record TelnetApiResult(
    int ApiVersion,
    string RequestId,
    bool Success,
    string Privilege,
    string PromptTerminator,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    long DurationMs,
    int SessionCount,
    int ReconnectCount,
    IReadOnlyList<TelnetApiCommandResult> Commands)
{
    public override string ToString() =>
        "TelnetApiResult { Commands = [REDACTED] }";
}

public sealed class StatelessTelnetRequest(
    string requestId,
    IPAddress address,
    string model,
    TelnetCredentials credentials,
    IReadOnlyList<string> commands,
    string purpose)
{
    public string RequestId { get; } = requestId;
    public IPAddress Address { get; } = address;
    public string Model { get; } = model;
    public TelnetCredentials Credentials { get; } = credentials;
    public IReadOnlyList<string> Commands { get; } = commands;
    public string Purpose { get; } = purpose;

    public override string ToString() =>
        "StatelessTelnetRequest { Endpoint = [REDACTED], Credentials = [REDACTED], Commands = [REDACTED] }";
}

public interface IStatelessTelnetExecutor
{
    Task<TelnetApiResult> ExecuteAsync(
        StatelessTelnetRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class CoreStatelessTelnetExecutor(
    IAdHocTelnetClient telnet,
    DeviceProfileRegistry profiles,
    AgentOptions options) : IStatelessTelnetExecutor
{
    public async Task<TelnetApiResult> ExecuteAsync(
        StatelessTelnetRequest request,
        CancellationToken cancellationToken = default)
    {
        var profile = profiles.GetRequired(request.Model);
        var stopwatch = Stopwatch.StartNew();
        var session = await telnet.ExecuteAsync(
                new TelnetEndpoint(request.Address.ToString(), 23),
                request.Credentials,
                profile.Telnet,
                request.Commands,
                cancellationToken)
            .ConfigureAwait(false);

        var outputs = new List<TelnetApiCommandResult>(session.Outputs.Count);
        foreach (var output in session.Outputs)
        {
            var truncated = Utf8OutputLimiter.Limit(output.NormalizedOutput, options.MaxOutputBytes);
            outputs.Add(new TelnetApiCommandResult(
                output.Command,
                truncated.Value,
                truncated.Truncated,
                output.CollectedAt));
        }

        return new TelnetApiResult(
            4,
            request.RequestId,
            true,
            session.Privilege == TelnetPrivilege.Privileged ? "privileged" : "user",
            session.PromptTerminator.ToString(),
            session.StartedAt,
            session.CompletedAt,
            stopwatch.ElapsedMilliseconds,
            session.SessionCount,
            session.ReconnectCount,
            outputs);
    }
}

public sealed class MockStatelessTelnetExecutor(TimeProvider? timeProvider = null) : IStatelessTelnetExecutor
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task<TelnetApiResult> ExecuteAsync(
        StatelessTelnetRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        var privilege = request.Credentials.EnablePassword is null ? "user" : "privileged";
        var outputs = request.Commands.Select(command => new TelnetApiCommandResult(
            command,
            "Synthetic mock Telnet output.",
            false,
            now)).ToArray();
        return Task.FromResult(new TelnetApiResult(
            4,
            request.RequestId,
            true,
            privilege,
            privilege == "privileged" ? "#" : ">",
            now,
            now,
            0,
            1,
            0,
            outputs));
    }
}

public sealed class TargetNetworkPolicy
{
    private readonly Ipv4Cidr[] _networks;

    public TargetNetworkPolicy(AgentOptions options)
    {
        _networks = options.AllowedTargetCidrs
            .Select(value => Ipv4Cidr.TryParse(value, out var cidr)
                ? cidr
                : throw new AgentConfigurationException(
                    AgentErrorCodes.ConfigurationInvalid,
                    "Allowed target CIDR is invalid."))
            .ToArray();
    }

    public bool TryValidate(string? host, int port, out IPAddress address)
    {
        address = IPAddress.None;
        if (port != 23 ||
            !Ipv4Cidr.TryParseStrictAddress(host, out var parsed) ||
            Ipv4Cidr.IsForbiddenTarget(parsed) ||
            !_networks.Any(network => network.Contains(parsed)))
        {
            return false;
        }

        address = parsed;
        return true;
    }
}

public sealed class TelnetExecutionAdmission(AgentOptions options)
{
    private readonly SemaphoreSlim _concurrency = new(
        options.MaxConcurrentExecutions,
        options.MaxConcurrentExecutions);
    private readonly ConcurrentDictionary<string, RateWindow> _rateWindows =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TargetGate> _targetGates =
        new(StringComparer.Ordinal);

    public async Task<IAsyncDisposable> EnterAsync(
        string clientAddress,
        IPAddress targetAddress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targetAddress);
        var now = DateTimeOffset.UtcNow;
        var window = _rateWindows.AddOrUpdate(
            clientAddress,
            _ => new RateWindow(now, 1),
            (_, current) => current.Add(now));
        if (window.Count > options.RateLimitPerMinute)
        {
            throw new AgentOperationException(
                AgentErrorCodes.QueryRateLimited,
                "Too many Telnet requests were submitted.",
                StatusCodes.Status429TooManyRequests);
        }

        var targetKey = targetAddress.ToString();
        TargetGate targetGate;
        while (true)
        {
            targetGate = _targetGates.GetOrAdd(targetKey, static _ => new TargetGate());
            lock (targetGate.Sync)
            {
                if (targetGate.Removed)
                {
                    continue;
                }

                targetGate.ReferenceCount++;
                break;
            }
        }

        var targetEntered = false;
        var globalEntered = false;
        try
        {
            targetEntered = await targetGate.Semaphore
                .WaitAsync(0, cancellationToken)
                .ConfigureAwait(false);
            if (!targetEntered)
            {
                throw Busy("The target switch already has an active Telnet request.");
            }

            globalEntered = await _concurrency
                .WaitAsync(0, cancellationToken)
                .ConfigureAwait(false);
            if (!globalEntered)
            {
                throw Busy("Agent is already handling the maximum number of Telnet requests.");
            }

            return new AdmissionLease(
                _concurrency,
                targetKey,
                targetGate,
                _targetGates);
        }
        catch
        {
            if (globalEntered)
            {
                _concurrency.Release();
            }
            if (targetEntered)
            {
                targetGate.Semaphore.Release();
            }
            ReleaseTargetReference(targetKey, targetGate, _targetGates);
            throw;
        }
    }

    private static AgentOperationException Busy(string message) =>
        new(
            AgentErrorCodes.AgentBusy,
            message,
            StatusCodes.Status503ServiceUnavailable);

    private static void ReleaseTargetReference(
        string targetKey,
        TargetGate targetGate,
        ConcurrentDictionary<string, TargetGate> targetGates)
    {
        var remove = false;
        lock (targetGate.Sync)
        {
            targetGate.ReferenceCount--;
            if (targetGate.ReferenceCount == 0)
            {
                targetGate.Removed = true;
                remove = true;
            }
        }

        if (!remove)
        {
            return;
        }

        targetGates.TryRemove(
            new KeyValuePair<string, TargetGate>(targetKey, targetGate));
        targetGate.Semaphore.Dispose();
    }

    private sealed record RateWindow(DateTimeOffset StartedUtc, int Count)
    {
        public RateWindow Add(DateTimeOffset now) =>
            now - StartedUtc >= TimeSpan.FromMinutes(1)
                ? new RateWindow(now, 1)
                : this with { Count = Count + 1 };
    }

    private sealed class TargetGate
    {
        public object Sync { get; } = new();
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
        public bool Removed { get; set; }
    }

    private sealed class AdmissionLease(
        SemaphoreSlim globalSemaphore,
        string targetKey,
        TargetGate targetGate,
        ConcurrentDictionary<string, TargetGate> targetGates) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                globalSemaphore.Release();
                targetGate.Semaphore.Release();
                ReleaseTargetReference(targetKey, targetGate, targetGates);
            }

            return ValueTask.CompletedTask;
        }
    }
}

public static class TelnetRequestValidator
{
    public static StatelessTelnetRequest Validate(
        TelnetApiRequest? request,
        bool isTest,
        TargetNetworkPolicy targetPolicy,
        DeviceProfileRegistry profiles,
        AgentOptions options)
    {
        if (request is null ||
            !IsRequestId(request.RequestId) ||
            !targetPolicy.TryValidate(request.Host, request.Port, out var address) ||
            string.IsNullOrWhiteSpace(request.Model) ||
            !profiles.TryGet(request.Model, out _) ||
            !IsPurpose(request.Purpose, isTest))
        {
            throw InvalidRequest(
                request is not null &&
                !string.IsNullOrWhiteSpace(request.Host) &&
                !targetPolicy.TryValidate(request.Host, request.Port, out _)
                    ? AgentErrorCodes.TargetNotAllowed
                    : AgentErrorCodes.RequestInvalid);
        }

        TelnetCredentials credentials;
        try
        {
            credentials = new TelnetCredentials(
                request.Username ?? string.Empty,
                request.Password ?? string.Empty,
                string.IsNullOrEmpty(request.EnablePassword) ? null : request.EnablePassword);
        }
        catch (ArgumentException)
        {
            throw InvalidRequest(AgentErrorCodes.RequestInvalid);
        }

        var suppliedCommands = request.Commands ?? [];
        if (isTest && suppliedCommands.Count != 0 ||
            !isTest && (suppliedCommands.Count == 0 ||
                        suppliedCommands.Count > options.MaxCommandsPerRequest))
        {
            throw InvalidRequest(AgentErrorCodes.RequestInvalid);
        }

        var normalized = new List<string>(suppliedCommands.Count);
        foreach (var command in suppliedCommands)
        {
            var validation = ReadOnlyQueryPolicy.Validate(command, options.MaxCommandLength);
            if (!validation.IsAllowed)
            {
                throw new AgentOperationException(
                    AgentErrorCodes.QueryCommandBlocked,
                    "Only a single-line show command is permitted.",
                    StatusCodes.Status400BadRequest);
            }
            normalized.Add(validation.NormalizedCommand!);
        }

        return new StatelessTelnetRequest(
            request.RequestId!,
            address,
            request.Model!,
            credentials,
            normalized,
            request.Purpose!.ToLowerInvariant());
    }

    private static bool IsRequestId(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_');

    private static bool IsPurpose(string? value, bool isTest) =>
        isTest
            ? string.Equals(value, "test", StringComparison.OrdinalIgnoreCase)
            : string.Equals(value, "manual", StringComparison.OrdinalIgnoreCase) ||
              string.Equals(value, "monitor", StringComparison.OrdinalIgnoreCase);

    private static AgentOperationException InvalidRequest(string code) =>
        new(
            code,
            code == AgentErrorCodes.TargetNotAllowed
                ? "Target IPv4 address or Telnet port is not allowed."
                : "Telnet request is invalid.",
            StatusCodes.Status400BadRequest);
}

internal static class Utf8OutputLimiter
{
    public static (string Value, bool Truncated) Limit(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes)
        {
            return (value, false);
        }

        var byteCount = 0;
        var characterCount = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (byteCount + runeBytes > maximumBytes)
            {
                break;
            }

            byteCount += runeBytes;
            characterCount += rune.Utf16SequenceLength;
        }
        return (value[..characterCount], true);
    }
}

public static class TelnetFailureMapper
{
    public static AgentOperationException Map(SwitchWatchException exception)
    {
        var (code, status, message) = exception.Error.Code switch
        {
            ErrorCodes.TcpTimeout =>
                (AgentErrorCodes.TcpTimeout, 504, "TCP connection timed out."),
            ErrorCodes.TelnetNegotiationFailed =>
                (AgentErrorCodes.TelnetNegotiationFailed, 502, "Telnet connection could not be established."),
            ErrorCodes.LoginPromptNotFound =>
                (AgentErrorCodes.LoginPromptNotFound, 504, "Login prompt was not received."),
            ErrorCodes.AuthFailed =>
                (AgentErrorCodes.AuthFailed, 401, "Switch authentication failed."),
            ErrorCodes.EnableFailed =>
                (AgentErrorCodes.EnableFailed, 403, "Switch privilege elevation failed."),
            ErrorCodes.CommandTimeout =>
                (AgentErrorCodes.CommandTimeout, 504, "Switch command timed out."),
            ErrorCodes.OutputLimitExceeded =>
                (AgentErrorCodes.OutputLimitExceeded, 502, "Switch response exceeded the safety limit."),
            ErrorCodes.TelnetSessionClosed =>
                (AgentErrorCodes.TelnetSessionClosed, 502, "Telnet session closed unexpectedly."),
            _ =>
                (AgentErrorCodes.PromptParseFailed, 502, "Switch response could not be processed.")
        };
        return new AgentOperationException(code, message, status);
    }
}
