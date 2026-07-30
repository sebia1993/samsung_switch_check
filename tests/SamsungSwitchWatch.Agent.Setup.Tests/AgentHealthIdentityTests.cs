using System.Net;
using System.Reflection;
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
    public void AgentHealthProbeResult_ExposesOnlySafeClassificationAndRestartFlag()
    {
        var properties = typeof(AgentHealthProbeResult)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["Code", "Ready", "RestartObserved"], properties);
    }

    [Fact]
    public void AgentHealthProbeCode_UsesStableFourBitSafeValues()
    {
        var values = Enum.GetValues<AgentHealthProbeCode>()
            .Select(value => (byte)value)
            .ToArray();

        Assert.Equal(values.Length, values.Distinct().Count());
        Assert.All(values, value => Assert.InRange(value, (byte)0, (byte)15));
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
    public async Task WaitUntilReadyAsync_ClassifiesHttpsFailureWithoutExceptionDetails()
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
    }
}
