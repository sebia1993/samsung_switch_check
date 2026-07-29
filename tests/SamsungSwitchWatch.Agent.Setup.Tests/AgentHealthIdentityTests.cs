using System.Net;
using System.Text;
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
    }

    [Theory]
    [InlineData("""{"status":"ready","apiVersion":3,"protocol":"https","productVersion":"0.10.0-poc"}""")]
    [InlineData("""{"status":"ready","apiVersion":4,"protocol":"http","productVersion":"0.10.0-poc"}""")]
    [InlineData("""{"status":"ready","apiVersion":4,"protocol":"https","productVersion":"0.9.23-poc"}""")]
    [InlineData("""{"apiVersion":4,"protocol":"https","productVersion":"0.10.0-poc"}""")]
    [InlineData("""{"status":"ready"}""")]
    [InlineData("""{"status":1,"apiVersion":4,"protocol":"https","productVersion":"0.10.0-poc"}""")]
    [InlineData("""{"status":"ready","apiVersion":4,"protocol":1,"productVersion":"0.10.0-poc"}""")]
    [InlineData("""{"status":"ready","apiVersion":4,"protocol":"https","productVersion":1}""")]
    public void IsExpectedReadiness_RejectsUnrelatedOrWrongAgent(string json)
    {
        Assert.False(HttpsAgentHealthProbe.IsExpectedReadiness(json, "0.10.0-poc"));
    }

    [Fact]
    public async Task WaitUntilReadyAsync_UsesOnlyBoundedReadyEndpointForVersionCheck()
    {
        var handler = new RecordingHandler(
            """{"status":"ready","apiVersion":4,"protocol":"https","productVersion":"0.10.0-poc+build"}""");
        var probe = new HttpsAgentHealthProbe(() => handler);

        var ready = await probe.WaitUntilReadyAsync(
            new Uri("https://127.0.0.1:18443/health/ready"),
            "0.10.0-poc",
            0,
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(ready);
        Assert.Equal(["/health/ready"], handler.RequestPaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitUntilReadyAsync_RejectsMalformedOrOversizedReadyPayload(
        bool oversized)
    {
        var payload = oversized
            ? new string('x', 20 * 1024)
            : "{";
        var handler = new RecordingHandler(payload);
        var probe = new HttpsAgentHealthProbe(() => handler);

        var ready = await probe.WaitUntilReadyAsync(
            new Uri("https://127.0.0.1:18443/health/ready"),
            "0.10.0-poc",
            0,
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None);

        Assert.False(ready);
        Assert.NotEmpty(handler.RequestPaths);
        Assert.All(handler.RequestPaths, path => Assert.Equal("/health/ready", path));
    }

    private sealed class RecordingHandler(string responseJson) : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    responseJson,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
