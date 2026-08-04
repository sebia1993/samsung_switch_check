using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using SamsungSwitchWatch.Agent.Configuration;
using SamsungSwitchWatch.Agent.Security;

namespace SamsungSwitchWatch.Agent.Tests;

[CollectionDefinition("Agent HTTPS loopback", DisableParallelization = true)]
public sealed class AgentHttpsLoopbackCollectionDefinition;

[Collection("Agent HTTPS loopback")]
public sealed class AgentHttpsCertificateIntegrationTests
{
    [Fact]
    public void ProductionBuildFailure_DoesNotLeaveWindowsUserKeyContainer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dataDirectory = NewDataDirectory();
        var keyFilesBefore = GetWindowsUserKeyFiles();
        try
        {
            var exception = Assert.Throws<InvalidOperationException>(() =>
                AgentApplication.Build(
                    ["--service"],
                    ProductionOverrides(dataDirectory),
                    _ => throw new InvalidOperationException("synthetic build failure")));

            Assert.Equal("synthetic build failure", exception.Message);
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }

        Assert.Equal(keyFilesBefore, GetWindowsUserKeyFiles());
    }

    [Fact]
    public async Task ProductionKestrel_ReturnsExpectedHttpsReadinessPayload()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(
            CanBindLoopbackHttpsPort(),
            "AGENT_HTTPS_TEST_PORT_18443_IN_USE");

        var dataDirectory = NewDataDirectory();
        var keyFilesBefore = GetWindowsUserKeyFiles();
        try
        {
            await using var app = AgentApplication.Build(
                ["--service"],
                ProductionOverrides(dataDirectory));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await app.StartAsync(timeout.Token);
            try
            {
                using var handler = new HttpClientHandler
                {
                    AllowAutoRedirect = false,
                    UseProxy = false,
                    ServerCertificateCustomValidationCallback =
                        static (_, _, _, _) => true
                };
                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(5)
                };
                using var response = await client.GetAsync(
                    "https://127.0.0.1:18443/health/ready",
                    timeout.Token);

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                await using var body = await response.Content.ReadAsStreamAsync(
                    timeout.Token);
                using var document = await JsonDocument.ParseAsync(
                    body,
                    cancellationToken: timeout.Token);
                var root = document.RootElement;
                Assert.Equal("ready", root.GetProperty("status").GetString());
                Assert.Equal(4, root.GetProperty("apiVersion").GetInt32());
                Assert.Equal("https", root.GetProperty("protocol").GetString());
            }
            finally
            {
                using var stopTimeout =
                    new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await app.StopAsync(stopTimeout.Token);
            }
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }

        Assert.Equal(keyFilesBefore, GetWindowsUserKeyFiles());
    }

    [Fact]
    public async Task ProductionIdentity_CompletesWindowsTlsServerHandshakeAndByteExchange()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var dataDirectory = NewDataDirectory();

        try
        {
            var options = new AgentOptions
            {
                ListenUrl = "https://127.0.0.1:18443",
                DataDirectory = dataDirectory,
                AllowedViewerIpv4 = "192.168.10.20",
                AllowedTargetCidrs = ["192.0.2.0/24"]
            };
            AgentOptionsValidator.ValidateAndNormalize(options, dataDirectory);

            using var identity = AgentIdentityStore.LoadOrCreate(options);
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var server = RunTlsServerAsync(
                listener,
                identity.Certificate,
                timeout.Token);

            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, port, timeout.Token);
            await using var clientTls = new SslStream(
                client.GetStream(),
                leaveInnerStreamOpen: false);
            await clientTls.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.Tls12,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                    RemoteCertificateValidationCallback =
                        static (_, _, _, _) => true
                },
                timeout.Token);

            await clientTls.WriteAsync(new byte[] { 0x2A }, timeout.Token);
            await clientTls.FlushAsync(timeout.Token);
            var response = new byte[1];
            await clientTls.ReadExactlyAsync(response, timeout.Token);

            Assert.Equal(SslProtocols.Tls12, clientTls.SslProtocol);
            Assert.Equal(0x7E, response[0]);
            await server;
        }
        finally
        {
            Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private static async Task RunTlsServerAsync(
        TcpListener listener,
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var serverTls = new SslStream(
            connection.GetStream(),
            leaveInnerStreamOpen: false);
        await serverTls.AuthenticateAsServerAsync(
            new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                ClientCertificateRequired = false,
                EnabledSslProtocols = SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            },
            cancellationToken);

        var request = new byte[1];
        await serverTls.ReadExactlyAsync(request, cancellationToken);
        Assert.Equal(SslProtocols.Tls12, serverTls.SslProtocol);
        Assert.Equal(0x2A, request[0]);

        await serverTls.WriteAsync(new byte[] { 0x7E }, cancellationToken);
        await serverTls.FlushAsync(cancellationToken);
    }

    private static bool CanBindLoopbackHttpsPort()
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, 18443);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            listener?.Stop();
        }
    }

    private static string NewDataDirectory()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            "SamsungSwitchWatch-AgentHttpsCertificateTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);
        return dataDirectory;
    }

    private static IReadOnlyDictionary<string, string?> ProductionOverrides(
        string dataDirectory) =>
        new Dictionary<string, string?>
        {
            ["Agent:ListenUrl"] = "https://127.0.0.1:18443",
            ["Agent:DataDirectory"] = dataDirectory,
            ["Agent:MockMode"] = "false",
            ["Agent:AllowedViewerIpv4"] = "192.168.10.20",
            ["Agent:AllowedTargetCidrs:0"] = "192.0.2.0/24"
        };

    private static string[] GetWindowsUserKeyFiles()
    {
        var keyDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Crypto",
            "Keys");
        return Directory.Exists(keyDirectory)
            ? Directory.GetFiles(keyDirectory)
                .Select(path => Path.GetFileName(path)!)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
    }
}
