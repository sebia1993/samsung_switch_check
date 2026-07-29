using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SamsungSwitchWatch.Viewer.Services;

internal sealed record LocalIpv4InterfaceAddress(
    string InterfaceId,
    NetworkInterfaceType InterfaceType,
    OperationalStatus OperationalStatus,
    IPAddress Address);

internal interface ILocalIpv4Discovery
{
    IReadOnlyList<IPAddress> DiscoverPrivateIpv4Addresses();
}

internal sealed class SystemLocalIpv4Discovery : ILocalIpv4Discovery
{
    public IReadOnlyList<IPAddress> DiscoverPrivateIpv4Addresses()
    {
        var addresses = new List<LocalIpv4InterfaceAddress>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties properties;
            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                addresses.Add(new LocalIpv4InterfaceAddress(
                    networkInterface.Id,
                    networkInterface.NetworkInterfaceType,
                    networkInterface.OperationalStatus,
                    unicast.Address));
            }
        }

        return BuildCandidates(addresses);
    }

    internal static IReadOnlyList<IPAddress> BuildCandidates(
        IEnumerable<LocalIpv4InterfaceAddress> addresses)
    {
        ArgumentNullException.ThrowIfNull(addresses);
        return addresses
            .Where(item =>
                item.OperationalStatus == OperationalStatus.Up
                && item.InterfaceType is not (
                    NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                && item.Address.AddressFamily == AddressFamily.InterNetwork
                && IsRfc1918(item.Address))
            .Select(item => item.Address)
            .Distinct()
            .OrderBy(address => Convert.ToHexString(address.GetAddressBytes()), StringComparer.Ordinal)
            .ToArray();
    }

    internal static bool IsRfc1918(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
               || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
               || bytes[0] == 192 && bytes[1] == 168;
    }
}

internal sealed record LocalAgentPreflightUpdate(
    string CandidateAddress,
    int CandidateNumber,
    int CandidateCount,
    AgentConnectionProbeUpdate? ProbeUpdate);

internal sealed record LocalAgentPreflightResult(
    bool Succeeded,
    ViewerSettings? SuccessfulSettings,
    AgentConnectionProbeResult ProbeResult,
    int CandidateCount);

internal interface ILocalAgentPreflight
{
    Task<LocalAgentPreflightResult> RunAsync(
        ViewerSettings baseSettings,
        IProgress<LocalAgentPreflightUpdate>? progress,
        CancellationToken cancellationToken);
}

internal sealed class LocalAgentPreflight : ILocalAgentPreflight
{
    internal const int DefaultMaxCandidateAttempts = 6;
    internal static readonly TimeSpan DefaultCandidateTimeout = TimeSpan.FromSeconds(7);
    internal static readonly TimeSpan DefaultOverallTimeout = TimeSpan.FromSeconds(30);

    private readonly ILocalIpv4Discovery _discovery;
    private readonly IAgentConnectionProbe _connectionProbe;
    private readonly int _maxCandidateAttempts;
    private readonly TimeSpan _candidateTimeout;
    private readonly TimeSpan _overallTimeout;

    public LocalAgentPreflight(
        ILocalIpv4Discovery discovery,
        IAgentConnectionProbe connectionProbe)
        : this(
            discovery,
            connectionProbe,
            DefaultMaxCandidateAttempts,
            DefaultCandidateTimeout,
            DefaultOverallTimeout)
    {
    }

    internal LocalAgentPreflight(
        ILocalIpv4Discovery discovery,
        IAgentConnectionProbe connectionProbe,
        int maxCandidateAttempts,
        TimeSpan candidateTimeout,
        TimeSpan overallTimeout)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _connectionProbe = connectionProbe ?? throw new ArgumentNullException(nameof(connectionProbe));
        _maxCandidateAttempts = maxCandidateAttempts > 0
            ? maxCandidateAttempts
            : throw new ArgumentOutOfRangeException(nameof(maxCandidateAttempts));
        _candidateTimeout = candidateTimeout > TimeSpan.Zero
            ? candidateTimeout
            : throw new ArgumentOutOfRangeException(nameof(candidateTimeout));
        _overallTimeout = overallTimeout >= candidateTimeout
            ? overallTimeout
            : throw new ArgumentOutOfRangeException(nameof(overallTimeout));
    }

    public async Task<LocalAgentPreflightResult> RunAsync(
        ViewerSettings baseSettings,
        IProgress<LocalAgentPreflightUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseSettings);
        cancellationToken.ThrowIfCancellationRequested();

        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(_overallTimeout);
        IReadOnlyList<IPAddress> candidates;
        try
        {
            var discovery = Task.Run(
                _discovery.DiscoverPrivateIpv4Addresses,
                CancellationToken.None);
            candidates = await discovery
                .WaitAsync(overall.Token)
                .ConfigureAwait(false);
        }
        catch (NetworkInformationException)
        {
            const string code = "LOCAL_PRIVATE_IPV4_DISCOVERY_FAILED";
            var detail = ViewerConnectionMessages.ForCode(code);
            return new LocalAgentPreflightResult(
                false,
                null,
                AgentConnectionProbeResult.Failure(
                    AgentConnectionProbeStage.Address,
                    code,
                    detail),
                0);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            const string code = "LOCAL_AGENT_PREFLIGHT_TIMEOUT";
            var detail = ViewerConnectionMessages.ForCode(code);
            return new LocalAgentPreflightResult(
                false,
                null,
                AgentConnectionProbeResult.Failure(
                    AgentConnectionProbeStage.Address,
                    code,
                    detail),
                0);
        }

        if (candidates.Count == 0)
        {
            const string code = "LOCAL_PRIVATE_IPV4_NOT_FOUND";
            var detail = ViewerConnectionMessages.ForCode(code);
            return new LocalAgentPreflightResult(
                false,
                null,
                AgentConnectionProbeResult.Failure(
                    AgentConnectionProbeStage.Address,
                    code,
                    detail),
                0);
        }

        var orderedCandidates = OrderAndBoundCandidates(candidates, baseSettings);
        if (orderedCandidates.Count == 0)
        {
            const string code = "LOCAL_PRIVATE_IPV4_NOT_FOUND";
            var detail = ViewerConnectionMessages.ForCode(code);
            return new LocalAgentPreflightResult(
                false,
                null,
                AgentConnectionProbeResult.Failure(
                    AgentConnectionProbeStage.Address,
                    code,
                    detail),
                0);
        }

        AgentConnectionProbeResult? lastResult = null;
        for (var index = 0; index < orderedCandidates.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (overall.IsCancellationRequested)
            {
                break;
            }

            var address = orderedCandidates[index].ToString();
            var candidateNumber = index + 1;
            var candidate = ViewerSettingsSanitizer.Copy(baseSettings);
            candidate.DemoMode = false;
            if (!ViewerSettingsSanitizer.TryBuildAgentUri(
                    address,
                    ViewerSettingsSanitizer.DefaultAgentPort.ToString(),
                    out var agentUri,
                    out var reason))
            {
                lastResult = AgentConnectionProbeResult.Failure(
                    AgentConnectionProbeStage.Address,
                    "VIEWER_CONFIGURATION_INVALID",
                    reason);
                continue;
            }

            candidate.AgentUri = agentUri;
            progress?.Report(new LocalAgentPreflightUpdate(
                address,
                candidateNumber,
                orderedCandidates.Count,
                null));
            IProgress<AgentConnectionProbeUpdate>? candidateProgress = progress is null
                ? null
                : new Progress<AgentConnectionProbeUpdate>(update =>
                    progress.Report(new LocalAgentPreflightUpdate(
                        address,
                        candidateNumber,
                        orderedCandidates.Count,
                        update)));

            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
            attempt.CancelAfter(_candidateTimeout);
            try
            {
                lastResult = await _connectionProbe.ProbeAsync(
                        candidate,
                        candidateProgress,
                        attempt.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                const string code = "AGENT_TIMEOUT";
                lastResult = AgentConnectionProbeResult.Failure(
                    AgentConnectionProbeStage.Tcp,
                    code,
                    ViewerConnectionMessages.ForCode(code));
                if (overall.IsCancellationRequested)
                {
                    break;
                }
            }
            if (lastResult.Succeeded)
            {
                return new LocalAgentPreflightResult(
                    true,
                    candidate,
                    lastResult,
                    orderedCandidates.Count);
            }
        }

        if (overall.IsCancellationRequested)
        {
            const string code = "LOCAL_AGENT_PREFLIGHT_TIMEOUT";
            lastResult = AgentConnectionProbeResult.Failure(
                AgentConnectionProbeStage.Tcp,
                code,
                ViewerConnectionMessages.ForCode(code));
        }
        else
        {
            lastResult ??= AgentConnectionProbeResult.Failure(
                AgentConnectionProbeStage.Address,
                "LOCAL_AGENT_PREFLIGHT_FAILED",
                ViewerConnectionMessages.ForCode("LOCAL_AGENT_PREFLIGHT_FAILED"));
        }

        return new LocalAgentPreflightResult(
            false,
            null,
            lastResult,
            orderedCandidates.Count);
    }

    private IReadOnlyList<IPAddress> OrderAndBoundCandidates(
        IReadOnlyList<IPAddress> candidates,
        ViewerSettings baseSettings)
    {
        IPAddress? current = null;
        if (Uri.TryCreate(
                ViewerSettingsSanitizer.NormalizeAgentUri(baseSettings.AgentUri),
                UriKind.Absolute,
                out var currentUri)
            && IPAddress.TryParse(currentUri.Host, out var parsed)
            && parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            current = parsed;
        }

        return candidates
            .Where(SystemLocalIpv4Discovery.IsRfc1918)
            .Distinct()
            .OrderByDescending(address => current is not null && address.Equals(current))
            .ThenBy(address => Convert.ToHexString(address.GetAddressBytes()), StringComparer.Ordinal)
            .Take(_maxCandidateAttempts)
            .ToArray();
    }
}
