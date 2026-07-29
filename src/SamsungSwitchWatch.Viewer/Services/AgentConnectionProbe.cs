using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Services;

internal enum AgentConnectionProbeStage
{
    Address,
    Dns,
    Tcp,
    Https,
    Identity
}

internal enum AgentConnectionProbeState
{
    Pending,
    Running,
    Succeeded,
    Failed
}

internal sealed record AgentConnectionProbeUpdate(
    AgentConnectionProbeStage Stage,
    AgentConnectionProbeState State,
    string Detail,
    string? ErrorCode = null);

internal sealed record AgentConnectionProbeStageSnapshot(
    AgentConnectionProbeStage Stage,
    AgentConnectionProbeState State,
    long DurationMs);

internal sealed record AgentConnectionProbeResult(
    bool Succeeded,
    AgentIdentityDto? Identity,
    AgentConnectionProbeStage? FailedStage,
    string? ErrorCode,
    string Detail)
{
    public IReadOnlyList<AgentConnectionProbeStageSnapshot> StageSnapshots { get; init; } = [];

    public static AgentConnectionProbeResult Success(AgentIdentityDto identity, string detail) =>
        new(true, identity, null, null, detail);

    public static AgentConnectionProbeResult Failure(
        AgentConnectionProbeStage stage,
        string errorCode,
        string detail,
        AgentIdentityDto? identity = null) =>
        new(false, identity, stage, errorCode, detail);
}

internal interface IAgentConnectionProbe
{
    Task<AgentConnectionProbeResult> ProbeAsync(
        ViewerSettings settings,
        IProgress<AgentConnectionProbeUpdate>? progress,
        CancellationToken cancellationToken);
}

internal interface IAgentNetworkProbe
{
    Task<IReadOnlyList<IPAddress>> ResolveIpv4Async(
        string host,
        CancellationToken cancellationToken);

    Task ConnectAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        CancellationToken cancellationToken);
}

internal interface IAgentIdentityProbe
{
    Task<AgentIdentityDto> GetIdentityAsync(
        ViewerSettings settings,
        Action certificateAccepted,
        CancellationToken cancellationToken);
}

internal sealed class AgentConnectionProbe : IAgentConnectionProbe
{
    internal static readonly TimeSpan NetworkStageTimeout = TimeSpan.FromSeconds(5);

    private readonly IAgentNetworkProbe _network;
    private readonly IAgentIdentityProbe _identity;
    private readonly string _viewerProductVersion;

    public AgentConnectionProbe()
        : this(
            new SystemAgentNetworkProbe(),
            new HttpAgentIdentityProbe(),
            AgentProductVersionPolicy.CurrentViewerVersion)
    {
    }

    internal AgentConnectionProbe(
        IAgentNetworkProbe network,
        IAgentIdentityProbe identity,
        string viewerProductVersion)
    {
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _viewerProductVersion = AgentProductVersionPolicy.Normalize(viewerProductVersion);
    }

    public async Task<AgentConnectionProbeResult> ProbeAsync(
        ViewerSettings settings,
        IProgress<AgentConnectionProbeUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var timingProgress = new AgentConnectionProbeTimingProgress(progress);
        var result = await ProbeCoreAsync(
                settings,
                timingProgress,
                cancellationToken)
            .ConfigureAwait(false);
        return result with { StageSnapshots = timingProgress.CreateSnapshot() };
    }

    private async Task<AgentConnectionProbeResult> ProbeCoreAsync(
        ViewerSettings settings,
        IProgress<AgentConnectionProbeUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Report(progress, AgentConnectionProbeStage.Address, AgentConnectionProbeState.Running,
            "Agent 주소 형식을 확인하고 있습니다.");
        var clean = ViewerSettingsSanitizer.Sanitize(settings);
        if (!ViewerSettingsSanitizer.IsValidForLiveConnection(clean, out _)
            || !Uri.TryCreate(clean.AgentUri, UriKind.Absolute, out var uri))
        {
            const string code = "VIEWER_CONNECTION_REQUIRED";
            var detail = ViewerConnectionMessages.ForCode(code);
            Report(progress, AgentConnectionProbeStage.Address, AgentConnectionProbeState.Failed, detail, code);
            return AgentConnectionProbeResult.Failure(
                AgentConnectionProbeStage.Address,
                code,
                detail);
        }

        Report(progress, AgentConnectionProbeStage.Address, AgentConnectionProbeState.Succeeded,
            $"HTTPS 포트 {ViewerSettingsSanitizer.DefaultAgentPort}을 사용합니다.");

        IReadOnlyList<IPAddress> addresses;
        Report(progress, AgentConnectionProbeStage.Dns, AgentConnectionProbeState.Running,
            IPAddress.TryParse(uri.Host, out _) ? "입력한 IPv4를 확인하고 있습니다." : "Agent PC 이름을 IPv4로 찾고 있습니다.");
        try
        {
            addresses = await RunNetworkStageAsync(
                    token => _network.ResolveIpv4Async(uri.Host, token),
                    cancellationToken)
                .ConfigureAwait(false);
            if (addresses.Count == 0)
            {
                throw new SocketException((int)SocketError.HostNotFound);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ReportFailure(
                AgentConnectionProbeStage.Dns,
                TranslateNetworkFailure(exception, "AGENT_DNS_FAILED"),
                progress);
        }

        Report(progress, AgentConnectionProbeStage.Dns, AgentConnectionProbeState.Succeeded,
            IPAddress.TryParse(uri.Host, out _)
                ? "Agent PC IPv4 형식을 확인했습니다."
                : "Agent PC 이름에서 IPv4 주소를 찾았습니다.");

        Report(progress, AgentConnectionProbeStage.Tcp, AgentConnectionProbeState.Running,
            "Agent PC의 TCP/18443 수신 여부를 확인하고 있습니다.");
        try
        {
            await RunNetworkStageAsync(
                    async token =>
                    {
                        await _network.ConnectAsync(
                                addresses,
                                ViewerSettingsSanitizer.DefaultAgentPort,
                                token)
                            .ConfigureAwait(false);
                        return true;
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ReportFailure(
                AgentConnectionProbeStage.Tcp,
                TranslateNetworkFailure(exception, "AGENT_UNREACHABLE"),
                progress);
        }

        Report(progress, AgentConnectionProbeStage.Tcp, AgentConnectionProbeState.Succeeded,
            "TCP/18443 연결에 성공했습니다.");

        var httpsReported = 0;
        Report(progress, AgentConnectionProbeStage.Https, AgentConnectionProbeState.Running,
            "HTTPS 보호와 저장된 Agent 신뢰 정보를 확인하고 있습니다.");
        try
        {
            var identity = await _identity.GetIdentityAsync(
                    clean,
                    () =>
                    {
                        if (Interlocked.Exchange(ref httpsReported, 1) == 0)
                        {
                            Report(progress, AgentConnectionProbeStage.Https,
                                AgentConnectionProbeState.Succeeded,
                                "HTTPS 보호 연결을 확인했습니다.");
                            Report(progress, AgentConnectionProbeStage.Identity,
                                AgentConnectionProbeState.Running,
                                "Agent API와 버전을 확인하고 있습니다.");
                        }
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (Volatile.Read(ref httpsReported) == 0)
            {
                const string code = "AGENT_RESPONSE_INVALID";
                var tlsDetail = "HTTPS 인증서 확인 결과를 받지 못했습니다. Agent와 Viewer 버전을 확인해 주세요.";
                Report(progress, AgentConnectionProbeStage.Https, AgentConnectionProbeState.Failed,
                    tlsDetail, code);
                return AgentConnectionProbeResult.Failure(
                    AgentConnectionProbeStage.Https,
                    code,
                    tlsDetail);
            }

            if (!AgentProductVersionPolicy.IsCompatible(
                    identity.ProductVersion,
                    _viewerProductVersion,
                    out var versionDetail))
            {
                const string code = "AGENT_VERSION_MISMATCH";
                Report(progress, AgentConnectionProbeStage.Identity, AgentConnectionProbeState.Failed,
                    versionDetail, code);
                return AgentConnectionProbeResult.Failure(
                    AgentConnectionProbeStage.Identity,
                    code,
                    versionDetail,
                    identity);
            }

            // The probe uses a sanitized snapshot so malformed settings cannot
            // leak into the transport. Copy only the validated TOFU pin back to
            // the candidate that SwitchClientAsync will use for its second
            // connection. This closes the gap where a certificate could change
            // between probing and applying settings and be trusted again.
            if (!clean.TryGetAgentTrustPin(out var validatedPin))
            {
                const string code = "AGENT_RESPONSE_INVALID";
                var pinDetail = ViewerConnectionMessages.ForCode(code);
                Report(progress, AgentConnectionProbeStage.Identity, AgentConnectionProbeState.Failed,
                    pinDetail, code);
                return AgentConnectionProbeResult.Failure(
                    AgentConnectionProbeStage.Identity,
                    code,
                    pinDetail);
            }
            settings.SetAgentTrustPin(validatedPin);

            var detail = $"Agent {identity.ProductVersion} · API v{identity.ApiVersion} 확인";
            Report(progress, AgentConnectionProbeStage.Identity, AgentConnectionProbeState.Succeeded, detail);
            return AgentConnectionProbeResult.Success(identity, detail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var typed = AgentClientErrors.Translate(exception);
            var failedStage = Volatile.Read(ref httpsReported) == 0
                ? AgentConnectionProbeStage.Https
                : AgentConnectionProbeStage.Identity;
            var detail = ViewerConnectionMessages.ForCode(typed.ErrorCode);
            Report(progress, failedStage, AgentConnectionProbeState.Failed, detail, typed.ErrorCode);
            return AgentConnectionProbeResult.Failure(
                failedStage,
                typed.ErrorCode,
                detail);
        }
    }

    private static async Task<T> RunNetworkStageAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NetworkStageTimeout);
        try
        {
            return await action(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Agent connection probe stage timed out.");
        }
    }

    private static AgentConnectionProbeResult ReportFailure(
        AgentConnectionProbeStage stage,
        AgentClientException failure,
        IProgress<AgentConnectionProbeUpdate>? progress)
    {
        var detail = ViewerConnectionMessages.ForCode(failure.ErrorCode);
        Report(progress, stage, AgentConnectionProbeState.Failed, detail, failure.ErrorCode);
        return AgentConnectionProbeResult.Failure(stage, failure.ErrorCode, detail);
    }

    private static AgentClientException TranslateNetworkFailure(Exception exception, string fallbackCode)
    {
        var typed = AgentClientErrors.Translate(exception);
        return typed.ErrorCode == "AGENT_UNREACHABLE" && fallbackCode != "AGENT_UNREACHABLE"
            ? new AgentClientException(fallbackCode, typed.SuggestedConnectionState, exception)
            : typed;
    }

    private static void Report(
        IProgress<AgentConnectionProbeUpdate>? progress,
        AgentConnectionProbeStage stage,
        AgentConnectionProbeState state,
        string detail,
        string? errorCode = null) =>
        progress?.Report(new AgentConnectionProbeUpdate(stage, state, detail, errorCode));
}

internal sealed class AgentConnectionProbeTimingProgress(
    IProgress<AgentConnectionProbeUpdate>? inner) : IProgress<AgentConnectionProbeUpdate>
{
    internal const long MaximumDurationMs = 300_000;

    private readonly object _gate = new();
    private readonly Dictionary<AgentConnectionProbeStage, long> _startedAt = [];
    private readonly Dictionary<AgentConnectionProbeStage, AgentConnectionProbeStageSnapshot> _snapshots = [];

    public void Report(AgentConnectionProbeUpdate value)
    {
        lock (_gate)
        {
            if (value.State == AgentConnectionProbeState.Running)
            {
                _startedAt[value.Stage] = Stopwatch.GetTimestamp();
                _snapshots[value.Stage] = new AgentConnectionProbeStageSnapshot(
                    value.Stage,
                    value.State,
                    0);
            }
            else
            {
                var durationMs = _startedAt.TryGetValue(value.Stage, out var startedAt)
                    ? ClampDuration(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds)
                    : 0;
                _snapshots[value.Stage] = new AgentConnectionProbeStageSnapshot(
                    value.Stage,
                    value.State,
                    durationMs);
            }
        }

        inner?.Report(value);
    }

    internal IReadOnlyList<AgentConnectionProbeStageSnapshot> CreateSnapshot()
    {
        lock (_gate)
        {
            return Enum.GetValues<AgentConnectionProbeStage>()
                .Select(stage => _snapshots.TryGetValue(stage, out var snapshot)
                    ? snapshot
                    : new AgentConnectionProbeStageSnapshot(
                        stage,
                        AgentConnectionProbeState.Pending,
                        0))
                .ToArray();
        }
    }

    private static long ClampDuration(double durationMs)
    {
        if (!double.IsFinite(durationMs) || durationMs <= 0)
        {
            return 0;
        }

        return Math.Clamp(
            checked((long)Math.Ceiling(durationMs)),
            0,
            MaximumDurationMs);
    }
}

internal sealed class SystemAgentNetworkProbe : IAgentNetworkProbe
{
    public async Task<IReadOnlyList<IPAddress>> ResolveIpv4Async(
        string host,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var parsed))
        {
            return parsed.AddressFamily == AddressFamily.InterNetwork ? [parsed] : [];
        }

        return (await Dns.GetHostAddressesAsync(
                    host,
                    AddressFamily.InterNetwork,
                    cancellationToken)
                .ConfigureAwait(false))
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Distinct()
            .ToArray();
    }

    public async Task ConnectAsync(
        IReadOnlyList<IPAddress> addresses,
        int port,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                await client.ConnectAsync(address, port, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is SocketException or IOException)
            {
                lastFailure = exception;
            }
        }

        throw lastFailure ?? new SocketException((int)SocketError.HostUnreachable);
    }
}

internal sealed class HttpAgentIdentityProbe : IAgentIdentityProbe
{
    public async Task<AgentIdentityDto> GetIdentityAsync(
        ViewerSettings settings,
        Action certificateAccepted,
        CancellationToken cancellationToken)
    {
        var validator = new CertificatePinValidator(settings, certificateAccepted);
        await using var client = new HttpAgentClient(settings, null, null, validator);
        await client.StartAsync(cancellationToken).ConfigureAwait(false);
        return await client.GetIdentityAsync(cancellationToken).ConfigureAwait(false);
    }
}

internal static class AgentProductVersionPolicy
{
    internal static string CurrentViewerVersion { get; } = ResolveCurrentViewerVersion();

    public static bool IsCompatible(
        string? agentVersion,
        string viewerVersion,
        out string detail)
    {
        var normalizedAgent = Normalize(agentVersion);
        var normalizedViewer = Normalize(viewerVersion);
        if (normalizedAgent.Length == 0)
        {
            detail =
                "Agent가 제품 버전 정보를 제공하지 않습니다. Agent를 먼저 같은 릴리스 버전으로 업데이트해 주세요.";
            return false;
        }

        if (normalizedViewer.Length > 0
            && string.Equals(normalizedAgent, normalizedViewer, StringComparison.OrdinalIgnoreCase))
        {
            detail = $"Agent와 Viewer 버전이 같습니다. ({normalizedAgent})";
            return true;
        }

        detail = $"Agent {normalizedAgent}와 Viewer {normalizedViewer} 버전이 다릅니다. 같은 릴리스의 두 프로그램을 사용해 주세요.";
        return false;
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        var metadataIndex = normalized.IndexOf('+');
        return metadataIndex >= 0 ? normalized[..metadataIndex] : normalized;
    }

    private static string ResolveCurrentViewerVersion()
    {
        var assembly = typeof(AgentProductVersionPolicy).Assembly;
        var value = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        return Normalize(value ?? assembly.GetName().Version?.ToString(3));
    }
}
