using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.Models;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerConnectionTests
{
    [Fact]
    public void DirectHttpHandler_DisablesProxyRedirectsAndWindowsCredentials()
    {
        using var handler = HttpAgentClient.CreateDirectHttpHandler();

        Assert.False(handler.UseProxy);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseDefaultCredentials);
        Assert.Null(handler.ServerCertificateCustomValidationCallback);
    }

    [Fact]
    public void DirectWebSocketProxy_BypassesEveryDestination()
    {
        var destination = new Uri("ws://agent.example.test:18443/hubs/events");

        Assert.True(DirectWebProxy.Instance.IsBypassed(destination));
        Assert.Equal(destination, DirectWebProxy.Instance.GetProxy(destination));
    }

    [Fact]
    public void ReadOnlyQuery_CoversTwoMaximumTelnetSessionsAndRetryOverhead()
    {
        Assert.Equal(TimeSpan.FromSeconds(510), HttpAgentClient.ReadOnlyQueryTimeout);
        Assert.True(HttpAgentClient.ReadOnlyQueryTimeout > (TimeSpan.FromSeconds(240) * 2) + TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task StartAsync_RevalidatesIdentityWhenIdentityIsAlreadyCached()
    {
        using var certificate = CreateCertificate();
        var fixture = CreateClientFixture(certificate);
        await using var client = fixture.Client;

        await client.StartAsync(CancellationToken.None);
        await client.StartAsync(CancellationToken.None);

        Assert.Equal(2, fixture.ControlHandler.RequestCount);
        Assert.All(fixture.ControlHandler.Requests, request =>
            Assert.Equal(AgentApiRoutes.IdentityV4, request.PathAndQuery));
    }

    [Fact]
    public async Task TestAndExecute_RevalidateAgentBeforeEveryTelnetRequest()
    {
        using var certificate = CreateCertificate();
        var fixture = CreateClientFixture(
            certificate,
            queryResponse: _ => JsonResponse(HttpStatusCode.OK, TelnetResultJson));
        await using var client = fixture.Client;

        await client.TestTelnetAsync(Target(), CancellationToken.None);
        await client.ExecuteTelnetAsync(
            new TelnetExecuteRequestDto(
                "execute-1",
                "192.0.2.10",
                23,
                "IES4224GP",
                "operator",
                "secret",
                null,
                "manual",
                ["show port status"]),
            CancellationToken.None);

        Assert.Equal(2, fixture.ControlHandler.RequestCount);
        Assert.Equal(2, fixture.QueryHandler.RequestCount);
    }

    [Fact]
    public async Task TransportFailureAndRecovery_PublishOfflineThenConnected()
    {
        using var certificate = CreateCertificate();
        var attempt = 0;
        var fixture = CreateClientFixture(
            certificate,
            controlResponse: _ =>
            {
                attempt++;
                if (attempt == 2)
                {
                    throw new HttpRequestException(
                        HttpRequestError.ConnectionError,
                        "synthetic connection failure");
                }
                return JsonResponse(HttpStatusCode.OK, IdentityJson(certificate));
            });
        await using var client = fixture.Client;
        var states = new List<AgentConnectionState>();
        client.ConnectionStateChanged += (_, state) => states.Add(state);

        await client.StartAsync(CancellationToken.None);
        var failure = await Assert.ThrowsAsync<AgentClientException>(
            () => client.StartAsync(CancellationToken.None));
        await client.StartAsync(CancellationToken.None);

        Assert.Equal("AGENT_UNREACHABLE", failure.ErrorCode);
        Assert.Contains(AgentConnectionState.Offline, states);
        var offlineIndex = states.IndexOf(AgentConnectionState.Offline);
        Assert.Contains(AgentConnectionState.Reconnecting, states.Skip(offlineIndex + 1));
        Assert.Contains(AgentConnectionState.Connected, states.Skip(offlineIndex + 1));
    }

    [Fact]
    public async Task ApplicationHttpError_DoesNotPublishOffline()
    {
        using var certificate = CreateCertificate();
        var fixture = CreateClientFixture(
            certificate,
            queryResponse: _ => JsonResponse(
                HttpStatusCode.BadRequest,
                """{"error":{"code":"QUERY_COMMAND_BLOCKED","message":"blocked"}}"""));
        await using var client = fixture.Client;
        var states = new List<AgentConnectionState>();
        client.ConnectionStateChanged += (_, state) => states.Add(state);

        var failure = await Assert.ThrowsAsync<AgentClientException>(
            () => client.TestTelnetAsync(Target(), CancellationToken.None));

        Assert.Equal("QUERY_COMMAND_BLOCKED", failure.ErrorCode);
        Assert.Contains(AgentConnectionState.Connected, states);
        Assert.DoesNotContain(AgentConnectionState.Offline, states);
    }

    [Theory]
    [InlineData("QUERY_DISABLED", "꺼져")]
    [InlineData("QUERY_COMMAND_BLOCKED", "show")]
    [InlineData("DEVICE_BUSY", "잠시")]
    [InlineData("QUERY_RATE_LIMITED", "요청")]
    [InlineData("QUERY_TIMEOUT", "초과")]
    [InlineData("AUTH_FAILED", "로그인")]
    public void ReadOnlyQueryErrors_AreActionableWithoutExposingRawOutput(string code, string expected)
    {
        var message = ViewerConnectionMessages.ForCode(code);

        Assert.Contains(expected, message, StringComparison.Ordinal);
        Assert.DoesNotContain("password", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("community", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QueryHttpError_PreservesStableAgentErrorCode()
    {
        var error = AgentClientErrors.FromStatus(
            HttpStatusCode.BadRequest,
            """{"error":{"code":"QUERY_COMMAND_BLOCKED","message":"blocked"}}""");

        Assert.Equal("QUERY_COMMAND_BLOCKED", error.ErrorCode);
        Assert.Equal(AgentConnectionState.Stale, error.SuggestedConnectionState);
    }

    [Theory]
    [InlineData("AGENT_DNS_FAILED", "이름")]
    [InlineData("AGENT_CONNECTION_REFUSED", "거부")]
    [InlineData("AGENT_TIMEOUT", "초과")]
    [InlineData("AGENT_ACCESS_DENIED", "방화벽")]
    [InlineData("AGENT_PROTOCOL_MISMATCH", "최신")]
    public void ConnectionMessages_AreActionableAndDoNotRequestSecrets(string code, string expected)
    {
        var message = ViewerConnectionMessages.ForCode(code);

        Assert.Contains(expected, message, StringComparison.Ordinal);
        Assert.DoesNotContain("지문", message, StringComparison.Ordinal);
        Assert.DoesNotContain("토큰", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Copy_PreservesWindowAndCursorStateWithoutSharingCursorDictionary()
    {
        var original = new ViewerSettings
        {
            DemoMode = false,
            AgentUri = "http://agent.example.test:18443",
            MainWidth = 1700,
            MainHeight = 950,
            StartMinimizedToTray = true,
            EventCursors = new Dictionary<string, long> { ["identity"] = 9 }
        };

        var copy = ViewerSettingsSanitizer.Copy(original);
        copy.EventCursors["identity"] = 10;

        Assert.Equal(original.AgentUri, copy.AgentUri);
        Assert.Equal(1700, copy.MainWidth);
        Assert.Equal(950, copy.MainHeight);
        Assert.True(copy.StartMinimizedToTray);
        Assert.Equal(9, original.EventCursors["identity"]);
    }

    private static TelnetTargetDto Target() => new(
        "test-1",
        "192.0.2.10",
        23,
        "IES4224GP",
        "operator",
        "secret",
        null,
        "connection-test");

    private static ClientFixture CreateClientFixture(
        X509Certificate2 certificate,
        Func<HttpRequestMessage, HttpResponseMessage>? controlResponse = null,
        Func<HttpRequestMessage, HttpResponseMessage>? queryResponse = null)
    {
        var settings = new ViewerSettings
        {
            DemoMode = false,
            AgentUri = "https://agent.example.test:18443"
        };
        var validator = new CertificatePinValidator(settings);
        Assert.True(validator.Validate(
            new HttpRequestMessage(HttpMethod.Get, settings.AgentUri),
            certificate,
            null,
            SslPolicyErrors.None));
        var control = new RecordingHandler(
            controlResponse ?? (_ => JsonResponse(HttpStatusCode.OK, IdentityJson(certificate))));
        var query = new RecordingHandler(
            queryResponse ?? (_ => JsonResponse(HttpStatusCode.OK, TelnetResultJson)));
        return new ClientFixture(
            new HttpAgentClient(settings, control, query, validator),
            control,
            query);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string IdentityJson(X509Certificate2 certificate) => $$"""
        {
          "apiVersion": 4,
          "agentId": "agent-test",
          "instanceId": "instance-test",
          "certificatePublicKeySha256": "{{CertificatePinValidator.GetSpkiSha256(certificate)}}",
          "protocol": "https",
          "maxCommandsPerRequest": 8,
          "maxOutputBytes": 65536
        }
        """;

    private const string TelnetResultJson = """
        {
          "apiVersion": 4,
          "requestId": "request-test",
          "success": true,
          "privilege": "user",
          "promptTerminator": "#",
          "startedUtc": "2026-07-23T00:00:00Z",
          "completedUtc": "2026-07-23T00:00:01Z",
          "durationMs": 1000,
          "commands": []
        }
        """;

    private static X509Certificate2 CreateCertificate()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=SamsungSwitchWatch.Test",
            key,
            HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddDays(1));
    }

    private sealed record ClientFixture(
        HttpAgentClient Client,
        RecordingHandler ControlHandler,
        RecordingHandler QueryHandler);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public List<(HttpMethod Method, string PathAndQuery)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            Requests.Add((request.Method, request.RequestUri?.PathAndQuery ?? string.Empty));
            return Task.FromResult(responseFactory(request));
        }
    }
}
