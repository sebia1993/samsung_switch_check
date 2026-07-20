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

    private static EventViewModel ViewModel(long sequence, string deviceId, string condition) =>
        new(new SwitchEventDto(
            sequence,
            $"event-{sequence}",
            deviceId,
            deviceId,
            DateTimeOffset.UtcNow,
            DeviceHealth.Critical,
            "state",
            "title",
            "detail",
            ConditionKey: condition,
            IsActiveCondition: true));
}
