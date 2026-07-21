using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerStabilityTests
{
    [Fact]
    public void BoundedAlertQueue_CoalescesConditionAndDropsOldestAtCapacity()
    {
        var queue = new BoundedAlertQueue(2);
        var now = DateTimeOffset.UtcNow;
        var first = ViewModel(1, "sw-1", "uplink");
        var duplicateCondition = ViewModel(2, "sw-1", "uplink");
        var second = ViewModel(3, "sw-2", "collector");
        var third = ViewModel(4, "sw-3", "poe");

        Assert.True(queue.Enqueue(first));
        Assert.False(queue.Enqueue(duplicateCondition));
        Assert.True(queue.Enqueue(second));
        Assert.True(queue.Enqueue(third));
        Assert.Equal(2, queue.Count);

        Assert.True(queue.TryDequeue(out var item));
        Assert.Equal("event-3", item!.AgentEventId);
        Assert.True(queue.TryDequeue(out item));
        Assert.Equal("event-4", item!.AgentEventId);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void ReconnectDelay_IsImmediateThenCappedAtSixtySecondsWithBoundedJitter()
    {
        Assert.Equal(TimeSpan.Zero, ReconnectDelay.GetDelay(0, 1));
        Assert.Equal(TimeSpan.FromSeconds(2), ReconnectDelay.GetDelay(1, 1));
        Assert.Equal(TimeSpan.FromSeconds(60), ReconnectDelay.GetDelay(500, 1));
        Assert.InRange(ReconnectDelay.GetDelay(500, 0.85), TimeSpan.FromSeconds(51), TimeSpan.FromSeconds(51));
        Assert.InRange(ReconnectDelay.GetDelay(500, 1.15), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void BoundedAlertQueue_CriticalPreemptsOneHundredWarningsWithinTwoSeconds()
    {
        var queue = new BoundedAlertQueue(20);
        for (var index = 0; index < 100; index++)
        {
            Assert.True(queue.Enqueue(ViewModel(index + 1, $"sw-{index}", $"warning-{index}", DeviceHealth.Warning)));
        }
        var critical = ViewModel(1001, "critical-sw", "uplink", DeviceHealth.Critical);
        var stopwatch = Stopwatch.StartNew();

        Assert.True(queue.Enqueue(critical));
        Assert.True(queue.TryDequeue(out var next));

        stopwatch.Stop();
        Assert.Same(critical, next);
        Assert.True(BoundedAlertQueue.ShouldPreempt(
            ViewModel(2001, "warning-sw", "warning", DeviceHealth.Warning), critical));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(19, queue.Count);
    }

    [Fact]
    public void BoundedAlertQueue_DeduplicatesConditionButKeepsRecoveryPhase()
    {
        var queue = new BoundedAlertQueue(5);
        var active = ViewModel(1, "sw", "uplink", DeviceHealth.Critical);
        var duplicate = ViewModel(2, "sw", "uplink", DeviceHealth.Critical);
        var recovery = ViewModel(3, "sw", "uplink", DeviceHealth.Normal, recovered: true);

        Assert.True(queue.Enqueue(active));
        Assert.False(queue.Enqueue(duplicate));
        Assert.True(queue.Enqueue(recovery));
        Assert.Equal(2, queue.Count);
        Assert.True(BoundedAlertQueue.ShouldPreempt(
            ViewModel(4, "other", "warning", DeviceHealth.Warning), recovery));
    }

    [Theory]
    [InlineData(AgentConnectionState.Offline, TrayIndicator.Offline, "오프라인")]
    [InlineData(AgentConnectionState.NeedsConnection, TrayIndicator.NeedsConnection, "연결 설정")]
    [InlineData(AgentConnectionState.Stale, TrayIndicator.Warning, "미확인")]
    [InlineData(AgentConnectionState.Reconnecting, TrayIndicator.Warning, "재연결")]
    public void TrayProjection_ConnectionStateOverridesHealthyCachedDevices(
        AgentConnectionState state,
        TrayIndicator indicator,
        string expectedText)
    {
        var received = new DateTimeOffset(2026, 7, 21, 3, 4, 0, TimeSpan.Zero);

        var projection = TrayStatusProjector.Create(state, 0, 0, 0, received);

        Assert.Equal(indicator, projection.Indicator);
        Assert.Contains(expectedText, projection.Text, StringComparison.Ordinal);
        Assert.Contains("07-21", projection.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("· 정상 ·", projection.Text, StringComparison.Ordinal);
        Assert.InRange(projection.Text.Length, 1, 63);
    }

    [Fact]
    public void AgentClientErrors_ClassifiesAccessReadinessAndSafeServerCodes()
    {
        var unauthorized = AgentClientErrors.FromStatus(HttpStatusCode.Unauthorized, "secret response");
        var forbidden = AgentClientErrors.FromStatus(HttpStatusCode.Forbidden);
        var unavailable = AgentClientErrors.FromStatus(HttpStatusCode.ServiceUnavailable,
            """{"error":{"code":"AGENT_DB_INTEGRITY_FAILED","message":"sensitive"}}""");
        var unsafeCode = AgentClientErrors.FromStatus(HttpStatusCode.ServiceUnavailable,
            """{"error":{"code":"unsafe secret value"}}""");

        Assert.Equal("AGENT_ACCESS_DENIED", unauthorized.ErrorCode);
        Assert.Equal(AgentConnectionState.Stale, forbidden.SuggestedConnectionState);
        Assert.Equal("AGENT_DB_INTEGRITY_FAILED", unavailable.ErrorCode);
        Assert.Equal(AgentConnectionState.Stale, unavailable.SuggestedConnectionState);
        Assert.Equal("AGENT_NOT_READY", unsafeCode.ErrorCode);
        Assert.DoesNotContain("secret", unauthorized.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AgentClientErrors_ClassifiesProtocolDnsRefusedTimeoutAndInvalidJsonWithoutRawMessage()
    {
        var protocol = AgentClientErrors.Translate(new HttpRequestException(
            HttpRequestError.SecureConnectionError, "raw protocol", null, null));
        var dns = AgentClientErrors.Translate(new HttpRequestException(
            HttpRequestError.NameResolutionError, "raw host", null, null));
        var refused = AgentClientErrors.Translate(new HttpRequestException(
            "raw endpoint", new SocketException((int)SocketError.ConnectionRefused)));
        var authentication = AgentClientErrors.Translate(new HttpRequestException(
            HttpRequestError.UserAuthenticationError, "raw identity", null, null));
        var timeout = AgentClientErrors.Translate(new TaskCanceledException("raw timeout"));
        var invalidJson = AgentClientErrors.Translate(new JsonException("raw response"));

        Assert.Equal("AGENT_PROTOCOL_MISMATCH", protocol.ErrorCode);
        Assert.Equal("AGENT_DNS_FAILED", dns.ErrorCode);
        Assert.Equal("AGENT_CONNECTION_REFUSED", refused.ErrorCode);
        Assert.Equal("AGENT_ACCESS_DENIED", authentication.ErrorCode);
        Assert.Equal("AGENT_TIMEOUT", timeout.ErrorCode);
        Assert.Equal("AGENT_RESPONSE_INVALID", invalidJson.ErrorCode);
        Assert.All(new[] { protocol, dns, refused, authentication, timeout, invalidJson }, error =>
            Assert.DoesNotContain("raw", error.Message, StringComparison.OrdinalIgnoreCase));
    }

    private static EventViewModel ViewModel(
        long sequence,
        string deviceId,
        string condition,
        DeviceHealth severity = DeviceHealth.Critical,
        bool recovered = false) =>
        new(new SwitchEventDto(
            sequence,
            $"event-{sequence}",
            deviceId,
            deviceId,
            DateTimeOffset.UtcNow,
            severity,
            "state",
            "title",
            "detail",
            Acknowledged: recovered,
            Recovered: recovered,
            ConditionKey: condition,
            IsActiveCondition: !recovered));
}
