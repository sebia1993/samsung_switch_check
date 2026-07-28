using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class AgentConnectionProbeTests
{
    [Fact]
    public async Task ProbeAsync_SuccessReportsEveryStageInOrder()
    {
        var progress = new InlineProgress();
        var probe = CreateProbe(
            identity: Identity("0.10.0-poc"),
            viewerVersion: "0.10.0-poc");
        var settings = Settings();

        var result = await probe.ProbeAsync(
            settings,
            progress,
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Identity);
        Assert.True(settings.TryGetAgentTrustPin(out var pin));
        Assert.Equal(new string('A', 64), pin);
        Assert.Equal(
            [
                (AgentConnectionProbeStage.Address, AgentConnectionProbeState.Running),
                (AgentConnectionProbeStage.Address, AgentConnectionProbeState.Succeeded),
                (AgentConnectionProbeStage.Dns, AgentConnectionProbeState.Running),
                (AgentConnectionProbeStage.Dns, AgentConnectionProbeState.Succeeded),
                (AgentConnectionProbeStage.Tcp, AgentConnectionProbeState.Running),
                (AgentConnectionProbeStage.Tcp, AgentConnectionProbeState.Succeeded),
                (AgentConnectionProbeStage.Https, AgentConnectionProbeState.Running),
                (AgentConnectionProbeStage.Https, AgentConnectionProbeState.Succeeded),
                (AgentConnectionProbeStage.Identity, AgentConnectionProbeState.Running),
                (AgentConnectionProbeStage.Identity, AgentConnectionProbeState.Succeeded)
            ],
            progress.Updates.Select(item => (item.Stage, item.State)).ToArray());
    }

    [Fact]
    public async Task ProbeAsync_DnsFailureStopsBeforeTcpAndUsesStableCode()
    {
        var progress = new InlineProgress();
        var network = new FakeNetworkProbe
        {
            Resolve = (_, _) => Task.FromException<IReadOnlyList<IPAddress>>(
                new SocketException((int)SocketError.HostNotFound))
        };
        var identity = new FakeIdentityProbe(Identity());
        var probe = new AgentConnectionProbe(network, identity, "0.10.0-poc");

        var result = await probe.ProbeAsync(Settings(), progress, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentConnectionProbeStage.Dns, result.FailedStage);
        Assert.Equal("AGENT_DNS_FAILED", result.ErrorCode);
        Assert.Equal(0, network.ConnectCalls);
        Assert.Equal(0, identity.Calls);
    }

    [Fact]
    public async Task ProbeAsync_ConnectionRefusedIdentifiesTcpStage()
    {
        var network = new FakeNetworkProbe
        {
            Connect = (_, _, _) => Task.FromException(
                new SocketException((int)SocketError.ConnectionRefused))
        };
        var probe = new AgentConnectionProbe(
            network,
            new FakeIdentityProbe(Identity()),
            "0.10.0-poc");

        var result = await probe.ProbeAsync(Settings(), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentConnectionProbeStage.Tcp, result.FailedStage);
        Assert.Equal("AGENT_CONNECTION_REFUSED", result.ErrorCode);
        Assert.Contains("서비스", result.Detail, StringComparison.Ordinal);
        Assert.Contains("TCP/18443", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeAsync_TcpTimeoutUsesStableTimeoutCode()
    {
        var network = new FakeNetworkProbe
        {
            Connect = (_, _, _) => Task.FromException(new TimeoutException("synthetic"))
        };
        var probe = new AgentConnectionProbe(
            network,
            new FakeIdentityProbe(Identity()),
            "0.10.0-poc");

        var result = await probe.ProbeAsync(Settings(), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentConnectionProbeStage.Tcp, result.FailedStage);
        Assert.Equal("AGENT_TIMEOUT", result.ErrorCode);
    }

    [Fact]
    public async Task ProbeAsync_TlsIdentityFailureIsReportedAtHttpsStage()
    {
        var identity = new FakeIdentityProbe(
            new AgentClientException(
                "AGENT_IDENTITY_CHANGED",
                AgentConnectionState.Stale),
            reportCertificateAccepted: false);
        var probe = new AgentConnectionProbe(
            new FakeNetworkProbe(),
            identity,
            "0.10.0-poc");

        var result = await probe.ProbeAsync(Settings(), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentConnectionProbeStage.Https, result.FailedStage);
        Assert.Equal("AGENT_IDENTITY_CHANGED", result.ErrorCode);
    }

    [Fact]
    public async Task ProbeAsync_InvalidApiAfterTlsIsReportedAtIdentityStage()
    {
        var identity = new FakeIdentityProbe(
            new AgentClientException(
                "AGENT_RESPONSE_INVALID",
                AgentConnectionState.Stale),
            reportCertificateAccepted: true);
        var probe = new AgentConnectionProbe(
            new FakeNetworkProbe(),
            identity,
            "0.10.0-poc");

        var result = await probe.ProbeAsync(Settings(), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentConnectionProbeStage.Identity, result.FailedStage);
        Assert.Equal("AGENT_RESPONSE_INVALID", result.ErrorCode);
    }

    [Fact]
    public async Task ProbeAsync_IdentityWithoutTlsAcceptanceFailsClosedAtHttpsStage()
    {
        var identity = new FakeIdentityProbe(
            Identity("0.10.0-poc"),
            reportCertificateAccepted: false,
            setSyntheticPin: true);
        var probe = new AgentConnectionProbe(
            new FakeNetworkProbe(),
            identity,
            "0.10.0-poc");

        var result = await probe.ProbeAsync(Settings(), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentConnectionProbeStage.Https, result.FailedStage);
        Assert.Equal("AGENT_RESPONSE_INVALID", result.ErrorCode);
    }

    [Fact]
    public async Task ProbeAsync_ProductVersionMismatchFailsClosedAtIdentityStage()
    {
        var probe = CreateProbe(
            identity: Identity("0.9.23-poc"),
            viewerVersion: "0.10.0-poc");

        var result = await probe.ProbeAsync(Settings(), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentConnectionProbeStage.Identity, result.FailedStage);
        Assert.Equal("AGENT_VERSION_MISMATCH", result.ErrorCode);
        Assert.Contains("0.9.23-poc", result.Detail, StringComparison.Ordinal);
        Assert.Contains("0.10.0-poc", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeAsync_MissingProductVersionFailsClosed()
    {
        var probe = CreateProbe(
            identity: Identity(),
            viewerVersion: "0.10.0-poc");

        var result = await probe.ProbeAsync(Settings(), null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(AgentConnectionProbeStage.Identity, result.FailedStage);
        Assert.Equal("AGENT_VERSION_MISMATCH", result.ErrorCode);
        Assert.Contains("제품 버전 정보", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProbeAsync_CopiesValidatedPinSoCertificateChangeBeforeApplyIsBlocked()
    {
        using var probeCertificate = CreateCertificate();
        using var changedCertificate = CreateCertificate();
        var settings = Settings();
        var probe = new AgentConnectionProbe(
            new FakeNetworkProbe(),
            new CertificateIdentityProbe(probeCertificate, "0.10.0-poc"),
            "0.10.0-poc");

        var result = await probe.ProbeAsync(settings, null, CancellationToken.None);
        var applyValidator = new CertificatePinValidator(settings);
        using var applyRequest = new HttpRequestMessage(HttpMethod.Get, settings.AgentUri);
        var acceptedChangedCertificate = applyValidator.Validate(
            applyRequest,
            changedCertificate,
            null,
            SslPolicyErrors.None);

        Assert.True(result.Succeeded);
        Assert.True(settings.TryGetAgentTrustPin(out var persistedPin));
        Assert.Equal(
            CertificatePinValidator.GetSpkiSha256(probeCertificate),
            persistedPin);
        Assert.False(acceptedChangedCertificate);
        Assert.True(applyValidator.IdentityChanged);
    }

    [Fact]
    public void ProductVersionPolicy_IgnoresSourceMetadataButNotReleaseDifference()
    {
        Assert.True(AgentProductVersionPolicy.IsCompatible(
            "0.10.0-poc+agentcommit",
            "0.10.0-poc+viewercommit",
            out _));
        Assert.False(AgentProductVersionPolicy.IsCompatible(
            "0.10.0-poc",
            "0.10.1-poc",
            out _));
    }

    private static AgentConnectionProbe CreateProbe(
        AgentIdentityDto identity,
        string viewerVersion) =>
        new(new FakeNetworkProbe(), new FakeIdentityProbe(identity, setSyntheticPin: true), viewerVersion);

    private static ViewerSettings Settings() => new()
    {
        AgentUri = "https://agent.example.test:18443"
    };

    private static AgentIdentityDto Identity(string? productVersion = null) =>
        new(
            4,
            "agent-test",
            "instance-test",
            new string('A', 64),
            "https",
            8,
            65_536)
        {
            ProductVersion = productVersion
        };

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=SamsungSwitchWatch.Probe.Test",
            key,
            HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed class InlineProgress : IProgress<AgentConnectionProbeUpdate>
    {
        public List<AgentConnectionProbeUpdate> Updates { get; } = [];

        public void Report(AgentConnectionProbeUpdate value) => Updates.Add(value);
    }

    private sealed class FakeNetworkProbe : IAgentNetworkProbe
    {
        public Func<string, CancellationToken, Task<IReadOnlyList<IPAddress>>> Resolve { get; init; } =
            (_, _) => Task.FromResult<IReadOnlyList<IPAddress>>([IPAddress.Parse("192.0.2.10")]);

        public Func<IReadOnlyList<IPAddress>, int, CancellationToken, Task> Connect { get; init; } =
            (_, _, _) => Task.CompletedTask;

        public int ConnectCalls { get; private set; }

        public Task<IReadOnlyList<IPAddress>> ResolveIpv4Async(
            string host,
            CancellationToken cancellationToken) =>
            Resolve(host, cancellationToken);

        public Task ConnectAsync(
            IReadOnlyList<IPAddress> addresses,
            int port,
            CancellationToken cancellationToken)
        {
            ConnectCalls++;
            return Connect(addresses, port, cancellationToken);
        }
    }

    private sealed class FakeIdentityProbe : IAgentIdentityProbe
    {
        private readonly AgentIdentityDto? _identity;
        private readonly Exception? _failure;
        private readonly bool _reportCertificateAccepted;
        private readonly bool _setSyntheticPin;

        public FakeIdentityProbe(
            AgentIdentityDto identity,
            bool reportCertificateAccepted = true,
            bool setSyntheticPin = true)
        {
            _identity = identity;
            _reportCertificateAccepted = reportCertificateAccepted;
            _setSyntheticPin = setSyntheticPin;
        }

        public FakeIdentityProbe(
            Exception failure,
            bool reportCertificateAccepted)
        {
            _failure = failure;
            _reportCertificateAccepted = reportCertificateAccepted;
        }

        public int Calls { get; private set; }

        public Task<AgentIdentityDto> GetIdentityAsync(
            ViewerSettings settings,
            Action certificateAccepted,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (_reportCertificateAccepted)
            {
                certificateAccepted();
            }
            if (_setSyntheticPin && _identity is not null)
            {
                settings.SetAgentTrustPin(_identity.CertificatePublicKeySha256);
            }

            return _failure is null
                ? Task.FromResult(_identity!)
                : Task.FromException<AgentIdentityDto>(_failure);
        }
    }

    private sealed class CertificateIdentityProbe(
        X509Certificate2 certificate,
        string productVersion) : IAgentIdentityProbe
    {
        public Task<AgentIdentityDto> GetIdentityAsync(
            ViewerSettings settings,
            Action certificateAccepted,
            CancellationToken cancellationToken)
        {
            var validator = new CertificatePinValidator(settings, certificateAccepted);
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.AgentUri);
            if (!validator.Validate(
                    request,
                    certificate,
                    null,
                    SslPolicyErrors.RemoteCertificateChainErrors))
            {
                throw new AgentClientException(
                    "AGENT_IDENTITY_CHANGED",
                    AgentConnectionState.Stale);
            }

            var pin = CertificatePinValidator.GetSpkiSha256(certificate);
            if (!validator.CompleteTrust(pin))
            {
                throw new AgentClientException(
                    "AGENT_IDENTITY_CHANGED",
                    AgentConnectionState.Stale);
            }

            return Task.FromResult(Identity(productVersion) with
            {
                CertificatePublicKeySha256 = pin
            });
        }
    }
}
