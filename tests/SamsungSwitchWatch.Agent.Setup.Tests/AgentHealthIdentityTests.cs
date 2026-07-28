using SamsungSwitchWatch.Agent.Setup.Infrastructure;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class AgentHealthIdentityTests
{
    [Theory]
    [InlineData("0.10.0-poc", "0.10.0-poc")]
    [InlineData("0.10.0-poc+abcdef", "0.10.0-poc")]
    public void IsExpectedIdentity_AcceptsApiV4HttpsAndNormalizedProductVersion(
        string actualVersion,
        string expectedVersion)
    {
        var json =
            $$"""{"apiVersion":4,"protocol":"https","productVersion":"{{actualVersion}}"}""";

        Assert.True(HttpsAgentHealthProbe.IsExpectedIdentity(json, expectedVersion));
    }

    [Theory]
    [InlineData("""{"apiVersion":3,"protocol":"https","productVersion":"0.10.0-poc"}""")]
    [InlineData("""{"apiVersion":4,"protocol":"http","productVersion":"0.10.0-poc"}""")]
    [InlineData("""{"apiVersion":4,"protocol":"https","productVersion":"0.9.23-poc"}""")]
    [InlineData("""{"status":"ready"}""")]
    public void IsExpectedIdentity_RejectsUnrelatedOrWrongAgent(string json)
    {
        Assert.False(HttpsAgentHealthProbe.IsExpectedIdentity(json, "0.10.0-poc"));
    }
}
