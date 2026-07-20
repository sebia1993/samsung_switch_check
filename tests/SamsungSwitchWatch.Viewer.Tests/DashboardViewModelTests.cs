using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;
using System.IO;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class DashboardViewModelTests
{
    [Fact]
    public void StatusAggregator_CountsEveryOperationalState()
    {
        var now = DateTimeOffset.UtcNow;
        var devices = new[]
        {
            Device("a", DeviceHealth.Normal, now),
            Device("b", DeviceHealth.Normal, now),
            Device("c", DeviceHealth.Warning, now),
            Device("d", DeviceHealth.Critical, now),
            Device("e", DeviceHealth.Disconnected, now),
            Device("f", DeviceHealth.Loading, now)
        }.Select(item => new DeviceViewModel(item));

        var result = StatusAggregator.Aggregate(devices);

        Assert.Equal(6, result.Total);
        Assert.Equal(2, result.Normal);
        Assert.Equal(1, result.Warning);
        Assert.Equal(1, result.Critical);
        Assert.Equal(1, result.Disconnected);
        Assert.Equal(1, result.Loading);
        Assert.Equal(3, result.ProblemCount);
    }

    [Fact]
    public async Task ApplyEvents_DeduplicatesAndKeepsNewestFirst()
    {
        using var fixture = new ViewModelFixture();
        var viewModel = fixture.CreateViewModel();
        var now = DateTimeOffset.UtcNow;

        viewModel.ApplyEvents([
            Event(7, now.AddSeconds(-2)),
            Event(9, now),
            Event(8, now.AddSeconds(-1)),
            Event(9, now)
        ]);

        Assert.Equal([9L, 8L, 7L], viewModel.RecentEvents.Select(item => item.Sequence));
        Assert.Equal(3, viewModel.UnacknowledgedCount);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task InitializeAsync_AppliesSnapshotAndOrderedCatchup()
    {
        using var fixture = new ViewModelFixture();
        var now = DateTimeOffset.UtcNow;
        fixture.Client.Snapshot = new AgentSnapshotDto(now, AgentConnectionState.Connected,
            [Device("a", DeviceHealth.Warning, now), Device("b", DeviceHealth.Normal, now)], 15, "1.0", "정상");
        fixture.Client.Events = [Event(13, now.AddSeconds(-2)), Event(15, now), Event(14, now.AddSeconds(-1)), Event(15, now)];
        var viewModel = fixture.CreateViewModel(new ViewerSettings { DemoMode = true, LastEventSequence = 12 });

        await viewModel.InitializeAsync();

        Assert.Equal(2, viewModel.TotalCount);
        Assert.Equal(1, viewModel.WarningCount);
        Assert.Equal([15L, 14L, 13L], viewModel.RecentEvents.Select(item => item.Sequence));
        Assert.Equal(AgentConnectionState.Connected, viewModel.ConnectionState);
        Assert.True(fixture.Client.StartCalled);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task LiveEvent_RepeatedSequenceDoesNotRepeatAlert()
    {
        using var fixture = new ViewModelFixture();
        var viewModel = fixture.CreateViewModel();
        var alertCount = 0;
        viewModel.AlertRaised += (_, _) => alertCount++;
        var item = Event(20, DateTimeOffset.UtcNow) with { Severity = DeviceHealth.Critical };

        fixture.Client.Emit(item);
        fixture.Client.Emit(item);

        Assert.Single(viewModel.RecentEvents);
        Assert.Equal(1, alertCount);
        await viewModel.DisposeAsync();
    }

    [Fact]
    public async Task EventUpdate_UsesAgentIdAndChangesAcknowledgementWithoutDuplicate()
    {
        using var fixture = new ViewModelFixture();
        var viewModel = fixture.CreateViewModel();
        var original = Event(21, DateTimeOffset.UtcNow) with { Severity = DeviceHealth.Critical };
        fixture.Client.Emit(original);

        fixture.Client.EmitUpdate(original with { Acknowledged = true });

        Assert.Single(viewModel.RecentEvents);
        Assert.True(viewModel.RecentEvents[0].Acknowledged);
        Assert.Equal("event-21", viewModel.RecentEvents[0].AgentEventId);
        await viewModel.DisposeAsync();
    }

    private static DeviceSnapshotDto Device(string id, DeviceHealth health, DateTimeOffset now) =>
        new(id, "SW-" + id.ToUpperInvariant(), "IES4224GP", "192.0.2.1", health, now, "summary", "1일", []);

    private static SwitchEventDto Event(long sequence, DateTimeOffset when) =>
        new(sequence, $"event-{sequence}", "a", "SW-A", when, DeviceHealth.Warning, "새 로그", "이벤트", "detail");

    private sealed class ViewModelFixture : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-ViewerTests", Guid.NewGuid().ToString("N"));
        public FakeAgentClient Client { get; } = new();

        public DashboardViewModel CreateViewModel(ViewerSettings? settings = null)
        {
            Directory.CreateDirectory(_folder);
            var store = new ViewerSettingsStore(Path.Combine(_folder, "settings.json"));
            return new DashboardViewModel(settings ?? new ViewerSettings { DemoMode = true }, store, new FakeFactory(Client));
        }

        public void Dispose()
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, true);
        }
    }

    private sealed class FakeFactory(FakeAgentClient client) : IAgentClientFactory
    {
        public IAgentClient Create(ViewerSettings settings) => client;
    }

    private sealed class FakeAgentClient : IAgentClient
    {
        public AgentSnapshotDto Snapshot { get; set; } = new(DateTimeOffset.UtcNow, AgentConnectionState.Demo, [], 0, "test", "test");
        public IReadOnlyList<SwitchEventDto> Events { get; set; } = [];
        public bool StartCalled { get; private set; }
        public event EventHandler<SwitchEventDto>? EventReceived;
        public event EventHandler<SwitchEventDto>? EventUpdated;
        public event EventHandler<AgentConnectionState>? ConnectionStateChanged;
        public Task StartAsync(CancellationToken cancellationToken) { StartCalled = true; ConnectionStateChanged?.Invoke(this, Snapshot.ConnectionState); return Task.CompletedTask; }
        public Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(Snapshot);
        public Task<IReadOnlyList<SwitchEventDto>> GetEventsAfterAsync(long sequence, CancellationToken cancellationToken) => Task.FromResult(Events);
        public Task<CommandResultDto> ExecuteRegisteredCheckAsync(string deviceId, string commandId, CancellationToken cancellationToken) => Task.FromResult(new CommandResultDto(true, "ok"));
        public Task<bool> AcknowledgeAsync(string eventId, CancellationToken cancellationToken) => Task.FromResult(true);
        public void Emit(SwitchEventDto item) => EventReceived?.Invoke(this, item);
        public void EmitUpdate(SwitchEventDto item) => EventUpdated?.Invoke(this, item);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
