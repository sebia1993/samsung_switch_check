using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Text;
using SamsungSwitchWatch.Agent.Setup.Deployment;
using SamsungSwitchWatch.Agent.Setup.Infrastructure;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class AgentHealthIdentityTests
{
    [Theory]
    [InlineData("0.10.0-poc", "0.10.0-poc")]
    [InlineData("0.10.0-poc+abcdef", "0.10.0-poc")]
    public void IsExpectedReadiness_AcceptsApiV4HttpsAndNormalizedProductVersion(
        string actualVersion,
        string expectedVersion)
    {
        var json =
            $$"""{"status":"ready","apiVersion":4,"protocol":"https","productVersion":"{{actualVersion}}"}""";

        Assert.True(HttpsAgentHealthProbe.IsExpectedReadiness(json, expectedVersion));
        Assert.Equal(
            AgentHealthProbeCode.Ready,
            HttpsAgentHealthProbe.ClassifyReadiness(json, expectedVersion));
    }

    [Theory]
    [InlineData(
        """{"status":"ready","apiVersion":3,"protocol":"https","productVersion":"0.10.0-poc"}""",
        AgentHealthProbeCode.ApiVersionMismatch)]
    [InlineData(
        """{"status":"ready","apiVersion":4,"protocol":"http","productVersion":"0.10.0-poc"}""",
        AgentHealthProbeCode.ProtocolMismatch)]
    [InlineData(
        """{"status":"ready","apiVersion":4,"protocol":"https","productVersion":"0.9.23-poc"}""",
        AgentHealthProbeCode.ProductVersionMismatch)]
    [InlineData(
        """{"apiVersion":4,"protocol":"https","productVersion":"0.10.0-poc"}""",
        AgentHealthProbeCode.PayloadInvalid)]
    [InlineData(
        """{"status":"ready"}""",
        AgentHealthProbeCode.PayloadInvalid)]
    [InlineData(
        """{"status":1,"apiVersion":4,"protocol":"https","productVersion":"0.10.0-poc"}""",
        AgentHealthProbeCode.PayloadInvalid)]
    [InlineData(
        """{"status":"ready","apiVersion":4,"protocol":1,"productVersion":"0.10.0-poc"}""",
        AgentHealthProbeCode.PayloadInvalid)]
    [InlineData(
        """{"status":"ready","apiVersion":4,"protocol":"https","productVersion":1}""",
        AgentHealthProbeCode.PayloadInvalid)]
    public void ClassifyReadiness_UsesStableSafeClassification(
        string json,
        AgentHealthProbeCode expected)
    {
        Assert.Equal(
            expected,
            HttpsAgentHealthProbe.ClassifyReadiness(json, "0.10.0-poc"));
        Assert.False(HttpsAgentHealthProbe.IsExpectedReadiness(json, "0.10.0-poc"));
    }

    [Theory]
    [InlineData(
        """{"status":"ready","agentId":"legacy-agent","apiVersion":4,"utc":"2026-07-30T00:00:00Z"}""",
        AgentHealthProbeCode.Ready)]
    [InlineData(
        """{"status":"ready","apiVersion":4,"protocol":"https","productVersion":"0.9.0-poc"}""",
        AgentHealthProbeCode.Ready)]
    [InlineData(
        """{"status":"ready","apiVersion":3,"protocol":"https","productVersion":"0.9.0-poc"}""",
        AgentHealthProbeCode.ApiVersionMismatch)]
    [InlineData(
        """{"status":"ready","apiVersion":4,"protocol":"http","productVersion":"0.9.0-poc"}""",
        AgentHealthProbeCode.ProtocolMismatch)]
    [InlineData(
        """{"status":"ready","apiVersion":4,"protocol":"https","productVersion":""}""",
        AgentHealthProbeCode.PayloadInvalid)]
    [InlineData(
        """{"status":"ready","apiVersion":4,"protocol":1}""",
        AgentHealthProbeCode.PayloadInvalid)]
    [InlineData(
        """{"status":"ready","apiVersion":4,"productVersion":1}""",
        AgentHealthProbeCode.PayloadInvalid)]
    public void ClassifyReadiness_WithoutExpectedVersionStillValidatesAgentContract(
        string json,
        AgentHealthProbeCode expected)
    {
        Assert.Equal(
            expected,
            HttpsAgentHealthProbe.ClassifyReadiness(
                json,
                expectedProductVersion: null));
    }

    [Fact]
    public void AgentHealthProbeResult_ExposesOnlySafeClassificationAndObservations()
    {
        var properties = typeof(AgentHealthProbeResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            [
                "Code",
                "HttpAttemptCount",
                "LastTransportPhase",
                "ListenerOwnedObserved",
                "Ready",
                "RestartObserved",
                "ServiceRunningObserved"
            ],
            properties);
    }

    [Fact]
    public void AgentHealthProbeCode_UsesStableDistinctByteValues()
    {
        var values = Enum.GetValues<AgentHealthProbeCode>()
            .Select(value => (byte)value)
            .ToArray();

        Assert.Equal(values.Length, values.Distinct().Count());
        Assert.Equal(
            Enumerable.Range(0, 19).Select(value => (byte)value),
            values);
    }

    [Fact]
    public void AgentHealthProbeResult_FailureRejectsReadyCode()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AgentHealthProbeResult.Failure(
                AgentHealthProbeCode.Ready,
                restartObserved: false));
    }

    [Fact]
    public async Task WaitUntilReadyAsync_UsesOnlyBoundedReadyEndpointForVersionCheck()
    {
        var handler = RecordingHandler.Json(
            """{"status":"ready","apiVersion":4,"protocol":"https","productVersion":"0.10.0-poc+build"}""");
        var observedProcessIds = new List<int>();
        var probe = CreateProbe(
            handler,
            (processId, _) =>
            {
                observedProcessIds.Add(processId);
                return HttpsAgentHealthProbe.ListenerOwnership.OwnedByExpectedProcess;
            });

        var result = await probe.WaitUntilReadyAsync(
            new Uri("https://127.0.0.1:18443/health/ready"),
            "0.10.0-poc",
            () => RunningService(4321),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(result.Ready);
        Assert.Equal(AgentHealthProbeCode.Ready, result.Code);
        Assert.False(result.RestartObserved);
        Assert.True(result.ServiceRunningObserved);
        Assert.True(result.ListenerOwnedObserved);
        Assert.Equal(1, result.HttpAttemptCount);
        Assert.Equal(
            AgentHealthTransportPhase.ReadinessValidated,
            result.LastTransportPhase);
        Assert.Equal([4321], observedProcessIds);
        Assert.Equal(["/health/ready"], handler.RequestPaths);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_WithoutExpectedVersionReadsAndValidatesReadyPayload()
    {
        var handler = RecordingHandler.Json(
            """{"status":"ready","agentId":"legacy-agent","apiVersion":4,"utc":"2026-07-30T00:00:00Z"}""");
        var probe = CreateProbe(
            handler,
            (_, _) => HttpsAgentHealthProbe.ListenerOwnership.OwnedByExpectedProcess);

        var result = await probe.WaitUntilReadyAsync(
            new Uri("https://127.0.0.1:18443/health/ready"),
            expectedProductVersion: null,
            () => RunningService(4321),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(result.Ready);
        Assert.Equal(AgentHealthProbeCode.Ready, result.Code);
        Assert.Equal(["/health/ready"], handler.RequestPaths);
    }

    [Theory]
    [InlineData(
        """{"status":"ready","apiVersion":3,"protocol":"https","productVersion":"0.9.0-poc"}""",
        AgentHealthProbeCode.ApiVersionMismatch)]
    [InlineData(
        """{"status":"ready","apiVersion":4,"protocol":"http","productVersion":"0.9.0-poc"}""",
        AgentHealthProbeCode.ProtocolMismatch)]
    [InlineData(
        """{"status":"ready","apiVersion":4,"protocol":"https","productVersion":""}""",
        AgentHealthProbeCode.PayloadInvalid)]
    [InlineData("{}", AgentHealthProbeCode.PayloadInvalid)]
    public async Task WaitUntilReadyAsync_WithoutExpectedVersionRejectsInvalidReadyPayload(
        string payload,
        AgentHealthProbeCode expected)
    {
        var handler = RecordingHandler.Json(payload);
        var probe = CreateProbe(
            handler,
            (_, _) => HttpsAgentHealthProbe.ListenerOwnership.OwnedByExpectedProcess);

        var result = await probe.WaitUntilReadyAsync(
            new Uri("https://127.0.0.1:18443/health/ready"),
            expectedProductVersion: null,
            () => RunningService(4321),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(expected, result.Code);
        Assert.NotEmpty(handler.RequestPaths);
        Assert.All(
            handler.RequestPaths,
            path => Assert.Equal("/health/ready", path));
    }

    [Fact]
    public async Task WaitUntilReadyAsync_AcceptsCurrentServiceAfterProcessIdChanges()
    {
        var handler = RecordingHandler.Json(
            """{"status":"ready","apiVersion":4,"protocol":"https","productVersion":"0.10.0-poc"}""");
        var snapshotCount = 0;
        var observedProcessIds = new List<int>();
        var probe = CreateProbe(
            handler,
            (processId, _) =>
            {
                observedProcessIds.Add(processId);
                return processId == 2002
                    ? HttpsAgentHealthProbe.ListenerOwnership.OwnedByExpectedProcess
                    : HttpsAgentHealthProbe.ListenerOwnership.NotListening;
            });

        var result = await probe.WaitUntilReadyAsync(
            new Uri("https://127.0.0.1:18443/health/ready"),
            "0.10.0-poc",
            () => RunningService(
                Interlocked.Increment(ref snapshotCount) == 1 ? 1001 : 2002),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(result.Ready);
        Assert.Equal(AgentHealthProbeCode.Ready, result.Code);
        Assert.True(result.RestartObserved);
        Assert.True(result.ServiceRunningObserved);
        Assert.True(result.ListenerOwnedObserved);
        Assert.Equal(1, result.HttpAttemptCount);
        Assert.Equal(
            AgentHealthTransportPhase.ReadinessValidated,
            result.LastTransportPhase);
        Assert.Equal([1001, 2002], observedProcessIds);
        Assert.Single(handler.RequestPaths);
    }

    [Theory]
    [InlineData(
        (int)HttpsAgentHealthProbe.ListenerOwnership.NotListening,
        AgentHealthProbeCode.TcpNotListening)]
    [InlineData(
        (int)HttpsAgentHealthProbe.ListenerOwnership.OwnedByOtherProcess,
        AgentHealthProbeCode.TcpOwnedByOtherProcess)]
    [InlineData(
        (int)HttpsAgentHealthProbe.ListenerOwnership.QueryFailed,
        AgentHealthProbeCode.TcpOwnershipQueryFailed)]
    public async Task WaitUntilReadyAsync_DoesNotContactUntrustedListener(
        int ownershipValue,
        AgentHealthProbeCode expected)
    {
        var ownership =
            (HttpsAgentHealthProbe.ListenerOwnership)ownershipValue;
        var handler = RecordingHandler.Json(
            """{"status":"ready","apiVersion":4,"protocol":"https","productVersion":"0.10.0-poc"}""");
        var probe = CreateProbe(handler, (_, _) => ownership);

        var result = await probe.WaitUntilReadyAsync(
            new Uri("https://127.0.0.1:18443/health/ready"),
            "0.10.0-poc",
            () => RunningService(4321),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(expected, result.Code);
        Assert.False(result.RestartObserved);
        Assert.True(result.ServiceRunningObserved);
        Assert.False(result.ListenerOwnedObserved);
        Assert.Equal(0, result.HttpAttemptCount);
        Assert.Equal(
            AgentHealthTransportPhase.NotStarted,
            result.LastTransportPhase);
        Assert.Empty(handler.RequestPaths);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_ClassifiesUnavailableServiceWithoutHttpRequest()
    {
        var handler = RecordingHandler.Json(
            """{"status":"ready","apiVersion":4,"protocol":"https","productVersion":"0.10.0-poc"}""");
        var probe = CreateProbe(
            handler,
            (_, _) => HttpsAgentHealthProbe.ListenerOwnership.OwnedByExpectedProcess);

        var result = await probe.WaitUntilReadyAsync(
            new Uri("https://127.0.0.1:18443/health/ready"),
            "0.10.0-poc",
            () => ServiceSnapshot.Missing,
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(AgentHealthProbeCode.ServiceUnavailable, result.Code);
        Assert.False(result.ServiceRunningObserved);
        Assert.False(result.ListenerOwnedObserved);
        Assert.Equal(0, result.HttpAttemptCount);
        Assert.Empty(handler.RequestPaths);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_ClassifiesServiceInspectionFailure()
    {
        var handler = RecordingHandler.Json(
            """{"status":"ready","apiVersion":4,"protocol":"https","productVersion":"0.10.0-poc"}""");
        var probe = CreateProbe(
            handler,
            (_, _) => HttpsAgentHealthProbe.ListenerOwnership.OwnedByExpectedProcess);

        var result = await probe.WaitUntilReadyAsync(
            new Uri("https://127.0.0.1:18443/health/ready"),
            "0.10.0-poc",
            () => throw new InvalidOperationException("must not escape"),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(AgentHealthProbeCode.ServiceInspectionFailed, result.Code);
        Assert.Empty(handler.RequestPaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitUntilReadyAsync_ClassifiesMalformedOrOversizedReadyPayload(
        bool oversized)
    {
        var payload = oversized
            ? new string('x', 20 * 1024)
            : "{";
        var handler = RecordingHandler.Json(payload);
        var probe = CreateProbe(
            handler,
            (_, _) => HttpsAgentHealthProbe.ListenerOwnership.OwnedByExpectedProcess);

        var result = await probe.WaitUntilReadyAsync(
            new Uri("https://127.0.0.1:18443/health/ready"),
            "0.10.0-poc",
            () => RunningService(4321),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(
            oversized
                ? AgentHealthProbeCode.PayloadTooLarge
                : AgentHealthProbeCode.PayloadInvalid,
            result.Code);
        Assert.NotEmpty(handler.RequestPaths);
        Assert.All(handler.RequestPaths, path => Assert.Equal("/health/ready", path));
    }

    [Fact]
    public async Task WaitUntilReadyAsync_ClassifiesHttpStatusWithoutReadingBody()
    {
        var handler = new RecordingHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    "sensitive body must not be returned",
                    Encoding.UTF8,
                    "text/plain")
            }));
        var probe = CreateProbe(
            handler,
            (_, _) => HttpsAgentHealthProbe.ListenerOwnership.OwnedByExpectedProcess);

        var result = await probe.WaitUntilReadyAsync(
            new Uri("https://127.0.0.1:18443/health/ready"),
            "0.10.0-poc",
            () => RunningService(4321),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(AgentHealthProbeCode.HttpStatusInvalid, result.Code);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_ClassifiesGenericHttpsFailureWithoutExceptionDetails()
    {
        var handler = new RecordingHandler(
            (_, _) => Task.FromException<HttpResponseMessage>(
                new HttpRequestException("sensitive transport detail")));
        var probe = CreateProbe(
            handler,
            (_, _) => HttpsAgentHealthProbe.ListenerOwnership.OwnedByExpectedProcess);

        var result = await probe.WaitUntilReadyAsync(
            new Uri("https://127.0.0.1:18443/health/ready"),
            "0.10.0-poc",
            () => RunningService(4321),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(AgentHealthProbeCode.HttpsRequestFailed, result.Code);
        Assert.True(result.ServiceRunningObserved);
        Assert.True(result.ListenerOwnedObserved);
        Assert.True(result.HttpAttemptCount > 0);
        Assert.Equal(
            AgentHealthTransportPhase.RequestStarted,
            result.LastTransportPhase);
    }

    [Theory]
    [InlineData(
        (int)HttpRequestError.SecureConnectionError,
        AgentHealthProbeCode.HttpsTlsFailed)]
    [InlineData(
        (int)HttpRequestError.ResponseEnded,
        AgentHealthProbeCode.HttpsEof)]
    [InlineData(
        (int)HttpRequestError.ConnectionError,
        AgentHealthProbeCode.HttpsConnectFailed)]
    [InlineData(
        (int)HttpRequestError.NameResolutionError,
        AgentHealthProbeCode.HttpsConnectFailed)]
    [InlineData(
        (int)HttpRequestError.Unknown,
        AgentHealthProbeCode.HttpsRequestFailed)]
    public void ClassifyTransportFailure_UsesSafeHttpRequestErrorCategory(
        int errorValue,
        AgentHealthProbeCode expected)
    {
        var exception = new HttpRequestException(
            (HttpRequestError)errorValue,
            "sensitive address and transport detail");

        Assert.Equal(
            expected,
            HttpsAgentHealthProbe.ClassifyTransportFailure(exception));
    }

    [Fact]
    public void ClassifyTransportFailure_AuthenticationExceptionOverridesGenericCategory()
    {
        var exception = new HttpRequestException(
            HttpRequestError.Unknown,
            "sensitive outer detail",
            new AuthenticationException("sensitive certificate detail"));

        Assert.Equal(
            AgentHealthProbeCode.HttpsTlsFailed,
            HttpsAgentHealthProbe.ClassifyTransportFailure(exception));
    }

    [Theory]
    [InlineData(
        (int)SocketError.ConnectionReset,
        AgentHealthProbeCode.HttpsConnectionReset)]
    [InlineData(
        (int)SocketError.ConnectionAborted,
        AgentHealthProbeCode.HttpsConnectionReset)]
    [InlineData(
        (int)SocketError.AccessDenied,
        AgentHealthProbeCode.HttpsConnectFailed)]
    [InlineData(
        (int)SocketError.TimedOut,
        AgentHealthProbeCode.HttpsRequestTimeout)]
    [InlineData(
        (int)SocketError.ConnectionRefused,
        AgentHealthProbeCode.HttpsConnectFailed)]
    public void ClassifyTransportFailure_UsesSafeSocketCategory(
        int socketError,
        AgentHealthProbeCode expected)
    {
        var exception = new HttpRequestException(
            HttpRequestError.ConnectionError,
            "sensitive outer detail",
            new SocketException(socketError));

        Assert.Equal(
            expected,
            HttpsAgentHealthProbe.ClassifyTransportFailure(exception));
    }

    [Theory]
    [InlineData(
        (int)HttpRequestError.ResponseEnded,
        -1,
        AgentHealthProbeCode.HttpsEof)]
    [InlineData(
        (int)HttpRequestError.ConnectionError,
        (int)SocketError.ConnectionReset,
        AgentHealthProbeCode.HttpsConnectionReset)]
    [InlineData(
        (int)HttpRequestError.ConnectionError,
        (int)SocketError.TimedOut,
        AgentHealthProbeCode.HttpsRequestTimeout)]
    public async Task WaitUntilReadyAsync_ClassifiesResponseBodyTransportFailure(
        int requestError,
        int socketError,
        AgentHealthProbeCode expected)
    {
        Exception? inner = socketError < 0
            ? null
            : new SocketException(socketError);
        var bodyFailure = new HttpIOException(
            (HttpRequestError)requestError,
            "sensitive response body detail",
            inner);
        var handler = new RecordingHandler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new ThrowingReadStream(bodyFailure))
            }));
        var probe = CreateProbe(
            handler,
            (_, _) =>
                HttpsAgentHealthProbe.ListenerOwnership.OwnedByExpectedProcess);

        var result = await probe.WaitUntilReadyAsync(
            new Uri("https://127.0.0.1:18443/health/ready"),
            "0.10.0-poc",
            () => RunningService(4321),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(expected, result.Code);
        Assert.True(result.ServiceRunningObserved);
        Assert.True(result.ListenerOwnedObserved);
        Assert.True(result.HttpAttemptCount > 0);
        Assert.Equal(
            AgentHealthTransportPhase.ResponseBody,
            result.LastTransportPhase);
    }

    [Theory]
    [InlineData(
        (int)HttpRequestError.SecureConnectionError,
        -1,
        AgentHealthProbeCode.HttpsTlsFailed)]
    [InlineData(
        (int)HttpRequestError.ResponseEnded,
        -1,
        AgentHealthProbeCode.HttpsEof)]
    [InlineData(
        (int)HttpRequestError.ConnectionError,
        (int)SocketError.ConnectionReset,
        AgentHealthProbeCode.HttpsConnectionReset)]
    [InlineData(
        (int)HttpRequestError.ConnectionError,
        (int)SocketError.AccessDenied,
        AgentHealthProbeCode.HttpsConnectFailed)]
    public async Task WaitUntilReadyAsync_ReturnsOnlySafeTransportClassification(
        int requestError,
        int socketError,
        AgentHealthProbeCode expected)
    {
        Exception? inner = socketError < 0
            ? null
            : new SocketException(socketError);
        var handler = new RecordingHandler(
            (_, _) => Task.FromException<HttpResponseMessage>(
                new HttpRequestException(
                    (HttpRequestError)requestError,
                    "sensitive address, certificate, and process detail",
                    inner)));
        var probe = CreateProbe(
            handler,
            (_, _) => HttpsAgentHealthProbe.ListenerOwnership.OwnedByExpectedProcess);

        var result = await probe.WaitUntilReadyAsync(
            new Uri("https://127.0.0.1:18443/health/ready"),
            "0.10.0-poc",
            () => RunningService(4321),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(expected, result.Code);
        Assert.True(result.ServiceRunningObserved);
        Assert.True(result.ListenerOwnedObserved);
        Assert.True(result.HttpAttemptCount > 0);
        Assert.Equal(
            AgentHealthTransportPhase.RequestStarted,
            result.LastTransportPhase);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_ClassifiesBoundedRequestTimeout()
    {
        var handler = new RecordingHandler(
            async (_, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("unreachable");
            });
        var probe = CreateProbe(
            handler,
            (_, _) => HttpsAgentHealthProbe.ListenerOwnership.OwnedByExpectedProcess);

        var result = await probe.WaitUntilReadyAsync(
            new Uri("https://127.0.0.1:18443/health/ready"),
            "0.10.0-poc",
            () => RunningService(4321),
            TimeSpan.FromMilliseconds(50),
            CancellationToken.None);

        Assert.False(result.Ready);
        Assert.Equal(AgentHealthProbeCode.HttpsRequestTimeout, result.Code);
        Assert.True(result.ServiceRunningObserved);
        Assert.True(result.ListenerOwnedObserved);
        Assert.Equal(1, result.HttpAttemptCount);
        Assert.Equal(
            AgentHealthTransportPhase.RequestStarted,
            result.LastTransportPhase);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_PreservesCumulativeSafeObservationsAcrossRetry()
    {
        var attempt = 0;
        var handler = new RecordingHandler(
            (_, _) =>
            {
                if (Interlocked.Increment(ref attempt) == 1)
                {
                    return Task.FromException<HttpResponseMessage>(
                        new HttpRequestException(
                            HttpRequestError.ConnectionError,
                            "sensitive EDR-like block",
                            new SocketException((int)SocketError.AccessDenied)));
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"status":"ready","apiVersion":4,"protocol":"https","productVersion":"0.10.0-poc"}""",
                        Encoding.UTF8,
                        "application/json")
                });
            });
        var probe = CreateProbe(
            handler,
            (_, _) => HttpsAgentHealthProbe.ListenerOwnership.OwnedByExpectedProcess);

        var result = await probe.WaitUntilReadyAsync(
            new Uri("https://127.0.0.1:18443/health/ready"),
            "0.10.0-poc",
            () => RunningService(4321),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(result.Ready);
        Assert.True(result.ServiceRunningObserved);
        Assert.True(result.ListenerOwnedObserved);
        Assert.Equal(2, result.HttpAttemptCount);
        Assert.Equal(
            AgentHealthTransportPhase.ReadinessValidated,
            result.LastTransportPhase);
    }

    [Fact]
    public async Task WaitUntilReadyAsync_UsesFreshClosedHttp11ConnectionForEveryAttempt()
    {
        var handlers = new List<AttemptHandler>();
        var attempt = 0;
        var probe = new HttpsAgentHealthProbe(
            () =>
            {
                var handler = new AttemptHandler(
                    Interlocked.Increment(ref attempt));
                handlers.Add(handler);
                return handler;
            },
            (_, _) =>
                HttpsAgentHealthProbe.ListenerOwnership.OwnedByExpectedProcess,
            TimeSpan.FromMilliseconds(1));

        var result = await probe.WaitUntilReadyAsync(
            new Uri("https://127.0.0.1:18443/health/ready"),
            "0.10.0-poc",
            () => RunningService(4321),
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(result.Ready);
        Assert.Equal(2, result.HttpAttemptCount);
        Assert.Equal(2, handlers.Count);
        Assert.All(handlers, handler => Assert.True(handler.Disposed));
        Assert.All(
            handlers,
            handler => Assert.Equal(HttpVersion.Version11, handler.RequestVersion));
        Assert.All(
            handlers,
            handler => Assert.Equal(
                HttpVersionPolicy.RequestVersionExact,
                handler.VersionPolicy));
        Assert.All(handlers, handler => Assert.True(handler.ConnectionClose));
    }

    [Fact]
    public async Task WaitUntilReadyAsync_PropagatesCallerCancellation()
    {
        var handler = RecordingHandler.Json(
            """{"status":"ready","apiVersion":4,"protocol":"https","productVersion":"0.10.0-poc"}""");
        var probe = CreateProbe(
            handler,
            (_, _) => HttpsAgentHealthProbe.ListenerOwnership.NotListening);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            probe.WaitUntilReadyAsync(
                new Uri("https://127.0.0.1:18443/health/ready"),
                "0.10.0-poc",
                () => RunningService(4321),
                TimeSpan.FromSeconds(1),
                cancellation.Token));
    }

    private static HttpsAgentHealthProbe CreateProbe(
        HttpMessageHandler handler,
        Func<int, int, HttpsAgentHealthProbe.ListenerOwnership> listenerOwnership) =>
        new(
            () => handler,
            listenerOwnership,
            TimeSpan.FromMilliseconds(1));

    private static ServiceSnapshot RunningService(int processId) =>
        ServiceSnapshot.Missing with
        {
            Exists = true,
            Running = true,
            ProcessId = processId
        };

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response)
        : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];

        public static RecordingHandler Json(string responseJson) =>
            new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseJson,
                    Encoding.UTF8,
                    "application/json")
            }));

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            return response(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            // Most existing tests intentionally reuse this recording double
            // across retries. Production handlers are still disposed per
            // attempt; disposal ownership is asserted by AttemptHandler.
        }
    }

    private sealed class AttemptHandler(int attempt) : HttpMessageHandler
    {
        public bool Disposed { get; private set; }
        public Version? RequestVersion { get; private set; }
        public HttpVersionPolicy VersionPolicy { get; private set; }
        public bool ConnectionClose { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestVersion = request.Version;
            VersionPolicy = request.VersionPolicy;
            ConnectionClose = request.Headers.ConnectionClose == true;
            if (attempt == 1)
            {
                return Task.FromException<HttpResponseMessage>(
                    new HttpRequestException(
                        HttpRequestError.ConnectionError,
                        "synthetic first-attempt failure"));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":"ready","apiVersion":4,"protocol":"https","productVersion":"0.10.0-poc"}""",
                    Encoding.UTF8,
                    "application/json")
            });
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingReadStream(Exception exception) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw exception;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(exception);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
