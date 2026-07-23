using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.Models;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

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
    public void CertificatePinMismatch_RemainsRecordedAfterLaterMatchingValidation()
    {
        using var expectedCertificate = CreateCertificate();
        using var changedCertificate = CreateCertificate();
        var settings = new ViewerSettings
        {
            AgentUri = "https://agent.example.test:18443"
        };
        settings.SetAgentTrustPin(CertificatePinValidator.GetSpkiSha256(expectedCertificate));
        var validator = new CertificatePinValidator(settings);
        using var request = new HttpRequestMessage(HttpMethod.Get, settings.AgentUri);

        Assert.False(validator.Validate(
            request,
            changedCertificate,
            null,
            SslPolicyErrors.None));
        Assert.True(validator.IdentityChanged);

        Assert.True(validator.Validate(
            request,
            expectedCertificate,
            null,
            SslPolicyErrors.None));
        Assert.True(validator.IdentityChanged);
    }

    [Fact]
    public void ReadOnlyQuery_CoversTwoMaximumTelnetSessionsAndRetryOverhead()
    {
        Assert.Equal(TimeSpan.FromSeconds(510), HttpAgentClient.ReadOnlyQueryTimeout);
        Assert.True(HttpAgentClient.ReadOnlyQueryTimeout > (TimeSpan.FromSeconds(240) * 2) + TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("0.9.8-poc+abcdef", "SamsungSwitchWatch.Viewer/0.9.8-poc")]
    [InlineData("1.2.3", "SamsungSwitchWatch.Viewer/1.2.3")]
    public void UserAgent_UsesInformationalVersionWithoutSourceMetadata(
        string informationalVersion,
        string expected)
    {
        Assert.Equal(
            expected,
            HttpAgentClient.CreateUserAgentValue(
                informationalVersion,
                new Version(9, 8, 7, 0)));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("invalid version")]
    public void UserAgent_InvalidInformationalVersionFallsBackToAssemblyVersion(
        string informationalVersion)
    {
        Assert.Equal(
            "SamsungSwitchWatch.Viewer/9.8.7",
            HttpAgentClient.CreateUserAgentValue(
                informationalVersion,
                new Version(9, 8, 7, 0)));
    }

    [Fact]
    public async Task ControlAndQueryRequestsUseCurrentViewerUserAgent()
    {
        using var certificate = CreateCertificate();
        var fixture = CreateClientFixture(certificate);
        await using var client = fixture.Client;

        await client.TestTelnetAsync(Target(), CancellationToken.None);

        var viewerAssembly = typeof(HttpAgentClient).Assembly;
        var expected = HttpAgentClient.CreateUserAgentValue(
            viewerAssembly
                .GetCustomAttributes(
                    typeof(System.Reflection.AssemblyInformationalVersionAttribute),
                    false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>()
                .Single()
                .InformationalVersion,
            viewerAssembly.GetName().Version);
        Assert.Equal(expected, HttpAgentClient.UserAgentValue);
        Assert.DoesNotContain("/0.8", HttpAgentClient.UserAgentValue, StringComparison.Ordinal);
        Assert.DoesNotContain("+", HttpAgentClient.UserAgentValue, StringComparison.Ordinal);
        Assert.Equal(
            [HttpAgentClient.UserAgentValue],
            fixture.ControlHandler.UserAgents);
        Assert.Equal(
            [HttpAgentClient.UserAgentValue],
            fixture.QueryHandler.UserAgents);
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
    public async Task TestAndExecute_ReuseValidatedAgentIdentity()
    {
        using var certificate = CreateCertificate();
        var fixture = CreateClientFixture(
            certificate,
            queryResponse: request =>
            {
                var isTest = request.RequestUri?.AbsolutePath == AgentApiRoutes.TelnetTestV4;
                return JsonResponse(
                    HttpStatusCode.OK,
                    TelnetResultJson(
                        isTest ? "test-1" : "execute-1",
                        isTest ? [] : ["show port status"]));
            });
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

        Assert.Equal(1, fixture.ControlHandler.RequestCount);
        Assert.Equal(2, fixture.QueryHandler.RequestCount);
    }

    [Fact]
    public async Task ConcurrentFirstTelnetRequests_ShareOneIdentityValidation()
    {
        using var certificate = CreateCertificate();
        using var identityRequestStarted = new ManualResetEventSlim();
        using var concurrentRequestReachedWait = new ManualResetEventSlim();
        using var releaseIdentityResponse = new ManualResetEventSlim();
        var fixture = CreateClientFixture(
            certificate,
            controlResponse: _ =>
            {
                identityRequestStarted.Set();
                if (!releaseIdentityResponse.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("Identity response was not released.");
                }
                return JsonResponse(HttpStatusCode.OK, IdentityJson(certificate));
            },
            queryResponse: request =>
            {
                var isTest = request.RequestUri?.AbsolutePath == AgentApiRoutes.TelnetTestV4;
                return JsonResponse(
                    HttpStatusCode.OK,
                    TelnetResultJson(
                        isTest ? "test-1" : "execute-1",
                        isTest ? [] : ["show port status"]));
            });
        await using var client = fixture.Client;

        var testTask = Task.Run(() =>
            client.TestTelnetAsync(Target(), CancellationToken.None));
        try
        {
            Assert.True(
                identityRequestStarted.Wait(TimeSpan.FromSeconds(10)),
                "The first request did not reach identity validation.");

            var executeTask = Task.Run(async () =>
            {
                var operation = client.ExecuteTelnetAsync(
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
                concurrentRequestReachedWait.Set();
                return await operation;
            });
            Assert.True(
                concurrentRequestReachedWait.Wait(TimeSpan.FromSeconds(10)),
                "The concurrent request did not reach identity validation.");
            Assert.False(executeTask.IsCompleted);
            Assert.Equal(1, fixture.ControlHandler.RequestCount);
            releaseIdentityResponse.Set();

            await Task.WhenAll(testTask, executeTask);
        }
        finally
        {
            releaseIdentityResponse.Set();
        }

        Assert.Equal(1, fixture.ControlHandler.RequestCount);
        Assert.Equal(2, fixture.QueryHandler.RequestCount);
    }

    [Fact]
    public async Task QueryTransportFailure_NextRequestRevalidatesIdentityOnce()
    {
        using var certificate = CreateCertificate();
        var queryAttempt = 0;
        var fixture = CreateClientFixture(
            certificate,
            queryResponse: _ =>
            {
                queryAttempt++;
                if (queryAttempt == 1)
                {
                    throw new HttpRequestException(
                        HttpRequestError.ConnectionError,
                        "synthetic query connection failure");
                }
                return JsonResponse(HttpStatusCode.OK, TelnetResultJson("test-1", []));
            });
        await using var client = fixture.Client;
        var states = new List<AgentConnectionState>();
        client.ConnectionStateChanged += (_, state) => states.Add(state);

        var failure = await Assert.ThrowsAsync<AgentClientException>(
            () => client.TestTelnetAsync(Target(), CancellationToken.None));
        var recovered = await client.TestTelnetAsync(Target(), CancellationToken.None);

        Assert.Equal("AGENT_UNREACHABLE", failure.ErrorCode);
        Assert.True(recovered.Success);
        Assert.Equal(2, fixture.ControlHandler.RequestCount);
        Assert.Equal(2, fixture.QueryHandler.RequestCount);
        Assert.Contains(AgentConnectionState.Offline, states);
        var offlineIndex = states.IndexOf(AgentConnectionState.Offline);
        Assert.Contains(AgentConnectionState.Reconnecting, states.Skip(offlineIndex + 1));
        Assert.Contains(AgentConnectionState.Connected, states.Skip(offlineIndex + 1));
    }

    [Fact]
    public async Task FailedExplicitIdentityRefresh_CannotReuseCachedValidation()
    {
        using var certificate = CreateCertificate();
        var controlAttempt = 0;
        var fixture = CreateClientFixture(
            certificate,
            controlResponse: _ =>
            {
                controlAttempt++;
                return controlAttempt == 2
                    ? JsonResponse(HttpStatusCode.NotFound, """{"error":{"code":"NOT_FOUND"}}""")
                    : JsonResponse(HttpStatusCode.OK, IdentityJson(certificate));
            });
        await using var client = fixture.Client;

        await client.StartAsync(CancellationToken.None);
        await Assert.ThrowsAsync<AgentClientException>(
            () => client.StartAsync(CancellationToken.None));
        var recovered = await client.TestTelnetAsync(Target(), CancellationToken.None);

        Assert.True(recovered.Success);
        Assert.Equal(3, fixture.ControlHandler.RequestCount);
        Assert.Equal(1, fixture.QueryHandler.RequestCount);
    }

    [Fact]
    public async Task IdentityBodyPinMismatch_BlocksTelnetBeforeQueryIsSent()
    {
        using var certificate = CreateCertificate();
        var fixture = CreateClientFixture(
            certificate,
            controlResponse: _ => JsonResponse(
                HttpStatusCode.OK,
                IdentityJson(certificate, new string('A', 64))));
        await using var client = fixture.Client;

        var failure = await Assert.ThrowsAsync<AgentClientException>(
            () => client.TestTelnetAsync(Target(), CancellationToken.None));

        Assert.Equal("AGENT_IDENTITY_CHANGED", failure.ErrorCode);
        Assert.Equal(AgentConnectionState.Stale, failure.SuggestedConnectionState);
        Assert.Equal(1, fixture.ControlHandler.RequestCount);
        Assert.Equal(0, fixture.QueryHandler.RequestCount);
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

        var firstFailure = await Assert.ThrowsAsync<AgentClientException>(
            () => client.TestTelnetAsync(Target(), CancellationToken.None));
        var secondFailure = await Assert.ThrowsAsync<AgentClientException>(
            () => client.TestTelnetAsync(Target(), CancellationToken.None));

        Assert.Equal("QUERY_COMMAND_BLOCKED", firstFailure.ErrorCode);
        Assert.Equal("QUERY_COMMAND_BLOCKED", secondFailure.ErrorCode);
        Assert.Equal(1, fixture.ControlHandler.RequestCount);
        Assert.Equal(2, fixture.QueryHandler.RequestCount);
        Assert.Contains(AgentConnectionState.Connected, states);
        Assert.DoesNotContain(AgentConnectionState.Offline, states);
    }

    [Fact]
    public async Task TelnetResponseWithMismatchedRequestId_FailsClosed()
    {
        using var certificate = CreateCertificate();
        var fixture = CreateClientFixture(
            certificate,
            queryResponse: _ => JsonResponse(
                HttpStatusCode.OK,
                TelnetResultJson("different-request", [])));
        await using var client = fixture.Client;

        var failure = await Assert.ThrowsAsync<AgentClientException>(
            () => client.TestTelnetAsync(Target(), CancellationToken.None));

        Assert.Equal("AGENT_RESPONSE_INVALID", failure.ErrorCode);
        Assert.Equal(AgentConnectionState.Stale, failure.SuggestedConnectionState);
    }

    [Fact]
    public async Task ExecuteTelnetAsync_RejectsDuplicateNormalizedCommandsBeforeSending()
    {
        using var certificate = CreateCertificate();
        var fixture = CreateClientFixture(certificate);
        await using var client = fixture.Client;
        var request = new TelnetExecuteRequestDto(
            "execute-1",
            "192.0.2.10",
            23,
            "IES4224GP",
            "operator",
            "secret",
            null,
            "manual",
            ["show  port status", "SHOW PORT STATUS"]);

        var failure = await Assert.ThrowsAsync<AgentClientException>(
            () => client.ExecuteTelnetAsync(request, CancellationToken.None));

        Assert.Equal("QUERY_COMMAND_BLOCKED", failure.ErrorCode);
        Assert.Equal(0, fixture.ControlHandler.RequestCount);
        Assert.Equal(0, fixture.QueryHandler.RequestCount);
    }

    [Fact]
    public async Task ExecuteTelnetAsync_AcceptsEightMaximumSizedCommandOutputs()
    {
        using var certificate = CreateCertificate();
        var commands = Enumerable.Range(1, 8).Select(index => $"show item {index}").ToArray();
        var fixture = CreateClientFixture(
            certificate,
            queryResponse: _ => JsonResponse(
                HttpStatusCode.OK,
                TelnetResultJson("execute-bulk", commands, new string('x', 65_536))));
        await using var client = fixture.Client;
        var request = new TelnetExecuteRequestDto(
            "execute-bulk",
            "192.0.2.10",
            23,
            "IES4224GP",
            "operator",
            "secret",
            null,
            "manual",
            commands);

        var result = await client.ExecuteTelnetAsync(request, CancellationToken.None);

        Assert.Equal(8, result.Commands.Count);
        Assert.All(result.Commands, item => Assert.Equal(65_536, Encoding.UTF8.GetByteCount(item.Output)));
    }

    [Fact]
    public async Task StartAsync_RejectsOversizedIdentityResponse()
    {
        using var certificate = CreateCertificate();
        var fixture = CreateClientFixture(
            certificate,
            controlResponse: _ => JsonResponse(
                HttpStatusCode.OK,
                new string('x', HttpAgentClient.MaximumIdentityResponseBytes + 1)));
        await using var client = fixture.Client;

        var failure = await Assert.ThrowsAsync<AgentClientException>(
            () => client.StartAsync(CancellationToken.None));

        Assert.Equal("AGENT_RESPONSE_INVALID", failure.ErrorCode);
    }

    [Fact]
    public async Task BoundedResponseReader_RejectsUnknownLengthStreamPastLimit()
    {
        using var content = new StreamContent(new MemoryStream(new byte[65]));
        content.Headers.ContentLength = null;

        var failure = await Assert.ThrowsAsync<InvalidDataException>(
            () => HttpAgentClient.ReadBoundedUtf8Async(content, 64, CancellationToken.None));

        Assert.Equal("AGENT_RESPONSE_TOO_LARGE", failure.Message);
    }

    [Fact]
    public void TelnetResponseBudget_CoversEightWorstCaseEscapedOutputs()
    {
        Assert.True(
            HttpAgentClient.MaximumTelnetResponseBytes
            > 8 * 65_536 * 6);
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

    [Theory]
    [InlineData("VIEWER_MONITOR_CYCLE_FAILED", "다음 주기")]
    [InlineData("VIEWER_SETTINGS_WRITE_FAILED", "디스크")]
    public void ViewerLocalFailureMessages_AreActionableAndSecretFree(
        string code,
        string expected)
    {
        var message = ViewerConnectionMessages.ForCode(code);

        Assert.Contains(expected, message, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.10", message, StringComparison.Ordinal);
        Assert.DoesNotContain("operator", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("show port status", message, StringComparison.OrdinalIgnoreCase);
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
            queryResponse ?? (_ => JsonResponse(HttpStatusCode.OK, TelnetResultJson("test-1", []))));
        return new ClientFixture(
            new HttpAgentClient(settings, control, query, validator),
            control,
            query);
    }

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, string json) => new(statusCode)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static string IdentityJson(
        X509Certificate2 certificate,
        string? publicKeySha256 = null)
    {
        publicKeySha256 ??= CertificatePinValidator.GetSpkiSha256(certificate);
        return $$"""
        {
          "apiVersion": 4,
          "agentId": "agent-test",
          "instanceId": "instance-test",
          "certificatePublicKeySha256": "{{publicKeySha256}}",
          "protocol": "https",
          "maxCommandsPerRequest": 8,
          "maxOutputBytes": 65536
        }
        """;
    }

    private static string TelnetResultJson(
        string requestId,
        IReadOnlyList<string> commands,
        string output = "ok") =>
        JsonSerializer.Serialize(new
        {
            apiVersion = 4,
            requestId,
            success = true,
            privilege = "user",
            promptTerminator = "#",
            startedUtc = "2026-07-23T00:00:00Z",
            completedUtc = "2026-07-23T00:00:01Z",
            durationMs = 1000,
            sessionCount = 1,
            reconnectCount = 0,
            commands = commands.Select(command => new
            {
                command,
                output,
                truncated = false,
                collectedUtc = "2026-07-23T00:00:01Z"
            })
        });

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
        private readonly ConcurrentQueue<(HttpMethod Method, string PathAndQuery)> _requests = [];
        private readonly ConcurrentQueue<string> _userAgents = [];
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);
        public IReadOnlyList<(HttpMethod Method, string PathAndQuery)> Requests =>
            _requests.ToArray();
        public IReadOnlyList<string> UserAgents => _userAgents.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            _requests.Enqueue((request.Method, request.RequestUri?.PathAndQuery ?? string.Empty));
            _userAgents.Enqueue(request.Headers.UserAgent.ToString());
            return Task.FromResult(responseFactory(request));
        }
    }
}
