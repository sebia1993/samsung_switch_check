using SamsungSwitchWatch.Viewer.Services;

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

    [Theory]
    [InlineData("AGENT_DNS_FAILED", "이름")]
    [InlineData("AGENT_CONNECTION_REFUSED", "거부")]
    [InlineData("AGENT_TIMEOUT", "초과")]
    [InlineData("AGENT_ACCESS_DENIED", "방화벽")]
    [InlineData("AGENT_PROTOCOL_MISMATCH", "v0.6")]
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
}
