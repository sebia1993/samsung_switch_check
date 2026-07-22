using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.Models;
using System.Net;

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
    public void ReadOnlyQuery_UsesDedicatedSeventySecondViewerTimeout()
    {
        Assert.Equal(TimeSpan.FromSeconds(70), HttpAgentClient.ReadOnlyQueryTimeout);
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
    [InlineData("AGENT_PROTOCOL_MISMATCH", "v0.7")]
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
