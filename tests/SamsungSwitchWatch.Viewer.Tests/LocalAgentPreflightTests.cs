using System.Net;
using System.Net.NetworkInformation;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class LocalAgentPreflightTests
{
    [Fact]
    public void Discovery_UsesOnlyActiveRfc1918Ipv4OutsideLoopbackAndTunnel()
    {
        var candidates = SystemLocalIpv4Discovery.BuildCandidates(
        [
            Address("ethernet", NetworkInterfaceType.Ethernet, OperationalStatus.Up, "10.20.30.40"),
            Address("wifi", NetworkInterfaceType.Wireless80211, OperationalStatus.Up, "192.168.10.20"),
            Address("private-b", NetworkInterfaceType.Ethernet, OperationalStatus.Up, "172.16.1.9"),
            Address("public", NetworkInterfaceType.Ethernet, OperationalStatus.Up, "203.0.113.10"),
            Address("link-local", NetworkInterfaceType.Ethernet, OperationalStatus.Up, "169.254.10.20"),
            Address("down", NetworkInterfaceType.Ethernet, OperationalStatus.Down, "10.1.1.2"),
            Address("loopback", NetworkInterfaceType.Loopback, OperationalStatus.Up, "10.1.1.3"),
            Address("tunnel", NetworkInterfaceType.Tunnel, OperationalStatus.Up, "10.1.1.4"),
            Address("ipv6", NetworkInterfaceType.Ethernet, OperationalStatus.Up, "fd00::1"),
            Address("duplicate", NetworkInterfaceType.Ethernet, OperationalStatus.Up, "10.20.30.40")
        ]);

        Assert.Equal(
            ["10.20.30.40", "172.16.1.9", "192.168.10.20"],
            candidates.Select(address => address.ToString()));
    }

    [Fact]
    public async Task RunAsync_TriesCandidatesSequentiallyWithoutCreatingTrustPins()
    {
        var discovery = new FakeDiscovery("10.1.2.3", "192.168.5.6");
        var probe = new SequencedProbe(failuresBeforeSuccess: 1);
        var preflight = new LocalAgentPreflight(discovery, probe);
        var original = new ViewerSettings { StartMinimizedToTray = true };

        var result = await preflight.RunAsync(
            original,
            null,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.CandidateCount);
        Assert.Equal(
            ["https://10.1.2.3:18443", "https://192.168.5.6:18443"],
            probe.AgentUris);
        Assert.Empty(original.AgentTrustPins);
        var successful = Assert.IsType<ViewerSettings>(result.SuccessfulSettings);
        Assert.Equal("https://192.168.5.6:18443", successful.AgentUri);
        Assert.Empty(successful.AgentTrustPins);
        Assert.True(successful.StartMinimizedToTray);
    }

    [Fact]
    public async Task RunAsync_AllFailuresDoNotMutateOriginalSettings()
    {
        var probe = new SequencedProbe(failuresBeforeSuccess: int.MaxValue);
        var preflight = new LocalAgentPreflight(
            new FakeDiscovery("10.1.2.3", "192.168.5.6"),
            probe);
        var original = new ViewerSettings
        {
            AgentUri = "https://agent.example.test:18443"
        };

        var result = await preflight.RunAsync(
            original,
            null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.SuccessfulSettings);
        Assert.Equal(2, result.CandidateCount);
        Assert.Equal("AGENT_CONNECTION_REFUSED", result.ProbeResult.ErrorCode);
        Assert.Equal("https://agent.example.test:18443", original.AgentUri);
        Assert.Empty(original.AgentTrustPins);
    }

    [Fact]
    public async Task RunAsync_NoPrivateAddressReturnsActionableFailureWithoutProbe()
    {
        var probe = new SequencedProbe(failuresBeforeSuccess: 0);
        var preflight = new LocalAgentPreflight(new FakeDiscovery(), probe);

        var result = await preflight.RunAsync(
            new ViewerSettings(),
            null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("LOCAL_PRIVATE_IPV4_NOT_FOUND", result.ProbeResult.ErrorCode);
        Assert.Contains("사설 IPv4", result.ProbeResult.Detail, StringComparison.Ordinal);
        Assert.Empty(probe.AgentUris);
    }

    [Fact]
    public async Task RunAsync_ManyCandidatesStopsAtSafetyLimit()
    {
        var addresses = Enumerable.Range(1, 10)
            .Select(index => $"10.20.30.{index}")
            .ToArray();
        var probe = new SequencedProbe(failuresBeforeSuccess: int.MaxValue);
        var preflight = new LocalAgentPreflight(
            new FakeDiscovery(addresses),
            probe);

        var result = await preflight.RunAsync(
            new ViewerSettings(),
            null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(LocalAgentPreflight.DefaultMaxCandidateAttempts, result.CandidateCount);
        Assert.Equal(LocalAgentPreflight.DefaultMaxCandidateAttempts, probe.AgentUris.Count);
    }

    [Fact]
    public async Task RunAsync_InternalTimeoutIsActionableAndDoesNotPersistCandidate()
    {
        var preflight = new LocalAgentPreflight(
            new FakeDiscovery("10.1.2.3"),
            new HangingProbe(),
            maxCandidateAttempts: 1,
            candidateTimeout: TimeSpan.FromMilliseconds(20),
            overallTimeout: TimeSpan.FromMilliseconds(500));
        var original = new ViewerSettings
        {
            AgentUri = "https://agent.example.test:18443"
        };

        var result = await preflight.RunAsync(
            original,
            null,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("AGENT_TIMEOUT", result.ProbeResult.ErrorCode);
        Assert.Equal("https://agent.example.test:18443", original.AgentUri);
        Assert.Empty(original.AgentTrustPins);
    }

    [Fact]
    public async Task RunAsync_DialogCancellationRemainsCancellation()
    {
        var preflight = new LocalAgentPreflight(
            new FakeDiscovery("10.1.2.3"),
            new HangingProbe(),
            maxCandidateAttempts: 1,
            candidateTimeout: TimeSpan.FromSeconds(1),
            overallTimeout: TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            preflight.RunAsync(
                new ViewerSettings(),
                null,
                cancellation.Token));
    }

    [Fact]
    public void RunAsync_DiscoveryRunsOffCallingThread()
    {
        var discovery = new ThreadRecordingDiscovery();
        var preflight = new LocalAgentPreflight(
            discovery,
            new SequencedProbe(failuresBeforeSuccess: 0));
        Exception? failure = null;
        LocalAgentPreflightResult? result = null;
        var callerThreadId = 0;
        var caller = new Thread(() =>
        {
            try
            {
                callerThreadId = Environment.CurrentManagedThreadId;
                result = preflight.RunAsync(
                        new ViewerSettings(),
                        null,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        })
        {
            IsBackground = true
        };

        caller.Start();

        Assert.True(caller.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
        Assert.NotNull(result);
        Assert.False(result.Succeeded);
        Assert.NotEqual(callerThreadId, discovery.DiscoveryThreadId);
    }

    [Fact]
    public async Task RunAsync_DiscoveryIsIncludedInOverallTimeout()
    {
        var discovery = new BlockingDiscovery();
        var probe = new SequencedProbe(failuresBeforeSuccess: 0);
        var preflight = new LocalAgentPreflight(
            discovery,
            probe,
            maxCandidateAttempts: 1,
            candidateTimeout: TimeSpan.FromMilliseconds(20),
            overallTimeout: TimeSpan.FromMilliseconds(80));

        try
        {
            var run = preflight.RunAsync(
                new ViewerSettings(),
                null,
                CancellationToken.None);

            Assert.True(discovery.Entered.Wait(TimeSpan.FromSeconds(5)));
            var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(result.Succeeded);
            Assert.Equal("LOCAL_AGENT_PREFLIGHT_TIMEOUT", result.ProbeResult.ErrorCode);
            Assert.Equal(AgentConnectionProbeStage.Address, result.ProbeResult.FailedStage);
            Assert.Empty(probe.AgentUris);
        }
        finally
        {
            discovery.Release.Set();
            Assert.True(discovery.Completed.Wait(TimeSpan.FromSeconds(5)));
        }
    }

    private static LocalIpv4InterfaceAddress Address(
        string id,
        NetworkInterfaceType type,
        OperationalStatus status,
        string address) =>
        new(id, type, status, IPAddress.Parse(address));

    private sealed class FakeDiscovery(params string[] addresses) : ILocalIpv4Discovery
    {
        public IReadOnlyList<IPAddress> DiscoverPrivateIpv4Addresses() =>
            addresses.Select(IPAddress.Parse).ToArray();
    }

    private sealed class ThreadRecordingDiscovery : ILocalIpv4Discovery
    {
        public int DiscoveryThreadId { get; private set; }

        public IReadOnlyList<IPAddress> DiscoverPrivateIpv4Addresses()
        {
            DiscoveryThreadId = Environment.CurrentManagedThreadId;
            return [];
        }
    }

    private sealed class BlockingDiscovery : ILocalIpv4Discovery
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public ManualResetEventSlim Completed { get; } = new();

        public IReadOnlyList<IPAddress> DiscoverPrivateIpv4Addresses()
        {
            Entered.Set();
            try
            {
                if (!Release.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Synthetic discovery was not released.");
                }

                return [IPAddress.Parse("10.1.2.3")];
            }
            finally
            {
                Completed.Set();
            }
        }
    }

    private sealed class SequencedProbe(int failuresBeforeSuccess) : IAgentConnectionProbe
    {
        private int _calls;

        public List<string> AgentUris { get; } = [];

        public Task<AgentConnectionProbeResult> ProbeAsync(
            ViewerSettings settings,
            IProgress<AgentConnectionProbeUpdate>? progress,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AgentUris.Add(settings.AgentUri);
            var call = Interlocked.Increment(ref _calls);
            if (call <= failuresBeforeSuccess)
            {
                return Task.FromResult(AgentConnectionProbeResult.Failure(
                    AgentConnectionProbeStage.Tcp,
                    "AGENT_CONNECTION_REFUSED",
                    ViewerConnectionMessages.ForCode("AGENT_CONNECTION_REFUSED")));
            }

            var identity = new AgentIdentityDto(
                4,
                "local-agent",
                "local-instance",
                new string('B', 64),
                "https",
                8,
                65_536)
            {
                ProductVersion = AgentProductVersionPolicy.CurrentViewerVersion
            };
            return Task.FromResult(AgentConnectionProbeResult.Success(
                identity,
                "Agent API와 버전을 확인했습니다."));
        }
    }

    private sealed class HangingProbe : IAgentConnectionProbe
    {
        public async Task<AgentConnectionProbeResult> ProbeAsync(
            ViewerSettings settings,
            IProgress<AgentConnectionProbeUpdate>? progress,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable");
        }
    }
}
