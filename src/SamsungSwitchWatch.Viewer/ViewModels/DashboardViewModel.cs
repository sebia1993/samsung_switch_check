using System.Collections.ObjectModel;
using System.Windows.Input;
using SamsungSwitchWatch.Viewer.Infrastructure;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.ViewModels;

public sealed record DeviceHealthSummary(int Total, int Normal, int Warning, int Critical, int Disconnected, int Loading)
{
    public int ProblemCount => Warning + Critical + Disconnected;
}

public static class StatusAggregator
{
    public static DeviceHealthSummary Aggregate(IEnumerable<DeviceViewModel> devices)
    {
        var items = devices.ToArray();
        return new DeviceHealthSummary(
            items.Length,
            items.Count(item => item.Health == DeviceHealth.Normal),
            items.Count(item => item.Health == DeviceHealth.Warning),
            items.Count(item => item.Health == DeviceHealth.Critical),
            items.Count(item => item.Health == DeviceHealth.Disconnected),
            items.Count(item => item.Health == DeviceHealth.Loading));
    }
}

public sealed class DashboardViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IAgentClientFactory _clientFactory;
    private readonly ViewerSettingsStore _settingsStore;
    private readonly SynchronizationContext? _uiContext;
    private readonly HashSet<long> _eventSequences = [];
    private IAgentClient _client;
    private ViewerSettings _settings;
    private DeviceViewModel? _selectedDevice;
    private EventViewModel? _selectedEvent;
    private AgentConnectionState _connectionState = AgentConnectionState.Connecting;
    private bool _isBusy;
    private string _operationMessage = "초기 상태를 불러오는 중입니다.";
    private string _collectorVersion = "-";
    private string _collectorSummary = "연결 준비 중";
    private DateTimeOffset? _lastRefreshedAt;
    private string _selectedCheckId = "interface_status";
    private bool _disposed;

    public DashboardViewModel(
        ViewerSettings settings,
        ViewerSettingsStore settingsStore,
        IAgentClientFactory? clientFactory = null,
        SynchronizationContext? synchronizationContext = null)
    {
        _settings = ViewerSettingsSanitizer.Sanitize(settings);
        _settings.BearerToken = settings.BearerToken;
        _settingsStore = settingsStore;
        _clientFactory = clientFactory ?? new AgentClientFactory();
        _uiContext = synchronizationContext ?? SynchronizationContext.Current;
        _client = _clientFactory.Create(_settings);
        SubscribeClient(_client);

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        ManualCheckCommand = new AsyncRelayCommand(ExecuteManualCheckAsync, () => !IsBusy && SelectedDevice is not null);
        AcknowledgeCommand = new RelayCommand<EventViewModel>(item => _ = AcknowledgeAsync(item), item => item is { Acknowledged: false });
        SelectDeviceCommand = new RelayCommand<DeviceViewModel>(item => SelectedDevice = item, item => item is not null);
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<EventViewModel> RecentEvents { get; } = [];
    public ObservableCollection<EventViewModel> SelectedDeviceLogs { get; } = [];
    public ObservableCollection<EventViewModel> SelectedDeviceChanges { get; } = [];
    public ObservableCollection<DeviceMetricDto> CollectorHealth { get; } = [];
    public IReadOnlyList<string> RegisteredChecks { get; } = ["interface_status", "system", "log_ram", "version"];

    public ICommand RefreshCommand { get; }
    public ICommand ManualCheckCommand { get; }
    public ICommand AcknowledgeCommand { get; }
    public ICommand SelectDeviceCommand { get; }

    public event EventHandler<EventViewModel>? AlertRaised;
    public ViewerSettings CurrentSettings => _settings;

    public DeviceViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                RebuildSelectedDeviceEvents();
                OnPropertyChanged(nameof(HasSelectedDevice));
            }
        }
    }

    public EventViewModel? SelectedEvent
    {
        get => _selectedEvent;
        set => SetProperty(ref _selectedEvent, value);
    }

    public bool HasSelectedDevice => SelectedDevice is not null;

    public AgentConnectionState ConnectionState
    {
        get => _connectionState;
        private set
        {
            if (SetProperty(ref _connectionState, value))
            {
                OnPropertyChanged(nameof(ConnectionText));
                OnPropertyChanged(nameof(ConnectionHealth));
            }
        }
    }

    public string ConnectionText => ConnectionState switch
    {
        AgentConnectionState.Connected => "Agent 연결됨",
        AgentConnectionState.Demo => "데모 모드",
        AgentConnectionState.Connecting => "연결 중",
        _ => "Agent 연결 끊김"
    };

    public DeviceHealth ConnectionHealth => ConnectionState switch
    {
        AgentConnectionState.Connected or AgentConnectionState.Demo => DeviceHealth.Normal,
        AgentConnectionState.Connecting => DeviceHealth.Loading,
        _ => DeviceHealth.Disconnected
    };

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(BusyVisibility));
            }
        }
    }

    public bool BusyVisibility => IsBusy;

    public string OperationMessage
    {
        get => _operationMessage;
        private set => SetProperty(ref _operationMessage, value);
    }

    public string CollectorVersion
    {
        get => _collectorVersion;
        private set => SetProperty(ref _collectorVersion, value);
    }

    public string CollectorSummary
    {
        get => _collectorSummary;
        private set => SetProperty(ref _collectorSummary, value);
    }

    public string LastRefreshText => _lastRefreshedAt is null
        ? "아직 점검하지 않음"
        : $"마지막 수신 {_lastRefreshedAt.Value.LocalDateTime:HH:mm:ss}";

    public string SelectedCheckId
    {
        get => _selectedCheckId;
        set => SetProperty(ref _selectedCheckId, value);
    }

    public int TotalCount => HealthSummary.Total;
    public int NormalCount => HealthSummary.Normal;
    public int WarningCount => HealthSummary.Warning;
    public int CriticalCount => HealthSummary.Critical;
    public int DisconnectedCount => HealthSummary.Disconnected;
    public int NewLogCount => RecentEvents.Count(item => !item.Acknowledged && IsLogEvent(item));
    public int UnacknowledgedCount => RecentEvents.Count(item => !item.Acknowledged);
    public DeviceHealthSummary HealthSummary => StatusAggregator.Aggregate(Devices);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        OperationMessage = "Agent 상태와 누락 이벤트를 동기화하는 중입니다.";
        try
        {
            var snapshot = await _client.GetSnapshotAsync(cancellationToken);
            ApplySnapshot(snapshot);
            await _client.StartAsync(cancellationToken);
            var catchup = await _client.GetEventsAfterAsync(_settings.LastEventSequence, cancellationToken);
            ApplyEvents(catchup, raiseAlerts: false);
            OperationMessage = _settings.DemoMode ? "오프라인 데모가 실행 중입니다." : "실시간 모니터링 중입니다.";
        }
        catch (Exception exception)
        {
            ConnectionState = AgentConnectionState.Disconnected;
            OperationMessage = $"Agent 연결 실패 · {SafeMessage(exception)}";
        }
        finally { IsBusy = false; }
    }

    public void ApplySnapshot(AgentSnapshotDto snapshot)
    {
        RunOnUi(() =>
        {
            ConnectionState = snapshot.ConnectionState;
            CollectorVersion = snapshot.CollectorVersion;
            CollectorSummary = snapshot.CollectorSummary;
            _lastRefreshedAt = snapshot.GeneratedAt;
            OnPropertyChanged(nameof(LastRefreshText));

            var incomingIds = snapshot.Devices.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in Devices.Where(item => !incomingIds.Contains(item.Id)).ToArray()) Devices.Remove(stale);
            foreach (var source in snapshot.Devices)
            {
                var existing = Devices.FirstOrDefault(item => item.Id == source.Id);
                if (existing is null) Devices.Add(new DeviceViewModel(source));
                else existing.Update(source);
            }

            SelectedDevice ??= Devices.FirstOrDefault();
            if (SelectedDevice is not null && !incomingIds.Contains(SelectedDevice.Id)) SelectedDevice = Devices.FirstOrDefault();
            RebuildCollectorHealth(snapshot.GeneratedAt);
            NotifySummaryChanged();
        });
    }

    public void ApplyEvents(IEnumerable<SwitchEventDto> events, bool raiseAlerts = true)
    {
        foreach (var item in events.OrderBy(item => item.Sequence))
        {
            RunOnUi(() => AddEvent(item, raiseAlerts));
        }
    }

    public async Task SwitchClientAsync(ViewerSettings settings, CancellationToken cancellationToken = default)
    {
        var clean = ViewerSettingsSanitizer.Sanitize(settings);
        clean.BearerToken = settings.BearerToken;
        var replacement = _clientFactory.Create(clean);
        var previous = _client;
        UnsubscribeClient(previous);
        _client = replacement;
        _settings = clean;
        _eventSequences.Clear();
        RecentEvents.Clear();
        SubscribeClient(replacement);
        await previous.DisposeAsync();
        _settingsStore.Save(_settings);
        await InitializeAsync(cancellationToken);
    }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        OperationMessage = "최신 상태를 불러오는 중입니다.";
        try
        {
            ApplySnapshot(await _client.GetSnapshotAsync(CancellationToken.None));
            ApplyEvents(await _client.GetEventsAfterAsync(_settings.LastEventSequence, CancellationToken.None), raiseAlerts: false);
            OperationMessage = "최신 상태로 갱신했습니다.";
        }
        catch (Exception exception)
        {
            ConnectionState = AgentConnectionState.Disconnected;
            OperationMessage = $"새로 고침 실패 · {SafeMessage(exception)}";
        }
        finally { IsBusy = false; }
    }

    private async Task ExecuteManualCheckAsync()
    {
        if (SelectedDevice is null) return;
        IsBusy = true;
        OperationMessage = $"{SelectedDevice.Name} · {SelectedCheckId} 점검 요청 중";
        try
        {
            var result = await _client.ExecuteRegisteredCheckAsync(SelectedDevice.Id, SelectedCheckId, CancellationToken.None);
            OperationMessage = result.Accepted ? result.Message : $"점검 거부 · {result.ErrorCode ?? "UNKNOWN"}";
        }
        catch (Exception exception) { OperationMessage = $"점검 요청 실패 · {SafeMessage(exception)}"; }
        finally { IsBusy = false; }
    }

    private async Task AcknowledgeAsync(EventViewModel? item)
    {
        if (item is null || item.Acknowledged) return;
        try
        {
            if (await _client.AcknowledgeAsync(item.AgentEventId, CancellationToken.None))
            {
                item.Acknowledged = true;
                NotifySummaryChanged();
            }
        }
        catch (Exception exception) { OperationMessage = $"확인 처리 실패 · {SafeMessage(exception)}"; }
    }

    private void AddEvent(SwitchEventDto source, bool raiseAlert)
    {
        if (source.Sequence <= 0 || !_eventSequences.Add(source.Sequence)) return;
        var item = new EventViewModel(source);
        var insertAt = 0;
        while (insertAt < RecentEvents.Count && RecentEvents[insertAt].Sequence > item.Sequence) insertAt++;
        RecentEvents.Insert(insertAt, item);
        while (RecentEvents.Count > 500)
        {
            _eventSequences.Remove(RecentEvents[^1].Sequence);
            RecentEvents.RemoveAt(RecentEvents.Count - 1);
        }

        _settings.LastEventSequence = Math.Max(_settings.LastEventSequence, item.Sequence);
        TryPersistSettings();
        if (SelectedDevice?.Id == item.DeviceId) RebuildSelectedDeviceEvents();
        NotifySummaryChanged();
        if (raiseAlert && !item.Acknowledged && item.Severity is DeviceHealth.Warning or DeviceHealth.Critical or DeviceHealth.Disconnected)
        {
            AlertRaised?.Invoke(this, item);
        }
    }

    private void OnClientEventReceived(object? sender, SwitchEventDto item) => RunOnUi(() => AddEvent(item, true));
    private void OnClientEventUpdated(object? sender, SwitchEventDto item) => RunOnUi(() => UpdateEvent(item));
    private void OnConnectionStateChanged(object? sender, AgentConnectionState state) => RunOnUi(() => ConnectionState = state);

    private void SubscribeClient(IAgentClient client)
    {
        client.EventReceived += OnClientEventReceived;
        client.EventUpdated += OnClientEventUpdated;
        client.ConnectionStateChanged += OnConnectionStateChanged;
    }

    private void UnsubscribeClient(IAgentClient client)
    {
        client.EventReceived -= OnClientEventReceived;
        client.EventUpdated -= OnClientEventUpdated;
        client.ConnectionStateChanged -= OnConnectionStateChanged;
    }

    private void RebuildSelectedDeviceEvents()
    {
        SelectedDeviceLogs.Clear();
        SelectedDeviceChanges.Clear();
        if (SelectedDevice is null) return;
        foreach (var item in RecentEvents.Where(item => item.DeviceId == SelectedDevice.Id))
        {
            if (IsLogEvent(item)) SelectedDeviceLogs.Add(item);
            else SelectedDeviceChanges.Add(item);
        }
    }

    private void UpdateEvent(SwitchEventDto source)
    {
        var existing = RecentEvents.FirstOrDefault(item =>
            string.Equals(item.AgentEventId, source.AgentEventId, StringComparison.Ordinal));
        if (existing is null)
        {
            AddEvent(source, false);
            return;
        }
        existing.Update(source);
        if (SelectedDevice?.Id == existing.DeviceId) RebuildSelectedDeviceEvents();
        NotifySummaryChanged();
    }

    private void RebuildCollectorHealth(DateTimeOffset generatedAt)
    {
        CollectorHealth.Clear();
        CollectorHealth.Add(new("Agent", ConnectionText, ConnectionHealth));
        CollectorHealth.Add(new("수집기 버전", CollectorVersion));
        CollectorHealth.Add(new("마지막 상태 생성", generatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")));
        CollectorHealth.Add(new("데이터 범위", "구조화 이벤트만 수신 · 원문 미수신"));
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(HealthSummary));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(NormalCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(CriticalCount));
        OnPropertyChanged(nameof(DisconnectedCount));
        OnPropertyChanged(nameof(NewLogCount));
        OnPropertyChanged(nameof(UnacknowledgedCount));
    }

    private void RunOnUi(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext) action();
        else _uiContext.Post(_ => action(), null);
    }

    private void TryPersistSettings()
    {
        try { _settingsStore.Save(_settings); } catch { /* Event flow must remain available if settings persistence fails. */ }
    }

    private static string SafeMessage(Exception exception) => exception switch
    {
        HttpRequestException => "AGENT_UNREACHABLE",
        TaskCanceledException => "AGENT_TIMEOUT",
        InvalidOperationException => "VIEWER_CONFIGURATION_INVALID",
        _ => "VIEWER_UNEXPECTED_ERROR"
    };

    private static bool IsLogEvent(EventViewModel item) =>
        item.Kind.Contains("로그", StringComparison.Ordinal) || item.Kind.Contains("log", StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        UnsubscribeClient(_client);
        await _client.DisposeAsync();
    }
}
