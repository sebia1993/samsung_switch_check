using System.Collections.ObjectModel;
using System.Text.Json;
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

public sealed record EventFilterOption(EventFilter Value, string Label);

public sealed class DashboardViewModel : ObservableObject, IAsyncDisposable
{
    private sealed record AlertCandidate(long ChangeSequence, string ChangeKind, SwitchEventDto Event);
    private enum AgentChannel { Http, Realtime }

    private const int EventPageSize = 500;
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(60);

    private readonly IAgentClientFactory _clientFactory;
    private readonly ViewerSettingsStore _settingsStore;
    private readonly SynchronizationContext? _uiContext;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _changeSync = new();
    private readonly object _settingsSync = new();
    private readonly Dictionary<string, EventViewModel> _eventsById = new(StringComparer.Ordinal);
    private readonly SortedDictionary<long, AgentEventChangeDto> _changeBuffer = [];
    private readonly HashSet<long> _liveAlertSequences = [];
    private IAgentClient _client;
    private ViewerSettings _settings;
    private DeviceViewModel? _selectedDevice;
    private EventViewModel? _selectedEvent;
    private AgentConnectionState _connectionState = AgentConnectionState.Connecting;
    private AgentConnectionState _httpConnectionState = AgentConnectionState.Connecting;
    private AgentConnectionState _realtimeConnectionState = AgentConnectionState.Connecting;
    private bool _isBusy;
    private string _operationMessage = "초기 상태를 불러오는 중입니다.";
    private string _collectorVersion = "-";
    private string _collectorSummary = "연결 준비 중";
    private DateTimeOffset? _lastRefreshedAt;
    private DateTimeOffset? _lastSuccessfulReceiptAt;
    private string _selectedCheckId = "interface_status";
    private string _currentAgentId = "agent";
    private string _eventSearchText = string.Empty;
    private EventFilterOption _selectedEventFilter;
    private long? _authoritativeUnacknowledged;
    private int _apiVersion = 2;
    private IReadOnlyList<OperationalStatusDto> _snapshotOperationalStatuses = [];
    private long _changeCursor;
    private long _settingsGeneration;
    private long _feedResetCount;
    private bool _hasSnapshot;
    private bool _allowLiveAlerts;
    private bool _initialized;
    private bool _disposed;
    private Task? _snapshotLoop;

    public DashboardViewModel(
        ViewerSettings settings,
        ViewerSettingsStore settingsStore,
        IAgentClientFactory? clientFactory = null,
        SynchronizationContext? synchronizationContext = null)
    {
        _settings = ViewerSettingsSanitizer.Sanitize(settings);
        _settingsStore = settingsStore;
        _clientFactory = clientFactory ?? new AgentClientFactory();
        _uiContext = synchronizationContext ?? SynchronizationContext.Current;
        _client = CreateInitialClient(_settings);
        SubscribeClient(_client);

        EventFilters =
        [
            new(EventFilter.All, "전체"),
            new(EventFilter.Unacknowledged, "미확인"),
            new(EventFilter.NewLog, "새 로그"),
            new(EventFilter.Critical, "장애"),
            new(EventFilter.Recovered, "복구")
        ];
        _selectedEventFilter = EventFilters[0];

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy && ConnectionState != AgentConnectionState.NeedsConnection);
        ManualCheckCommand = new AsyncRelayCommand(ExecuteManualCheckAsync, () => !IsBusy && SelectedDevice is not null && ConnectionState != AgentConnectionState.NeedsConnection);
        AcknowledgeCommand = new RelayCommand<EventViewModel>(item => _ = AcknowledgeAsync(item), item => item is { Acknowledged: false });
        SelectDeviceCommand = new RelayCommand<DeviceViewModel>(item => SelectedDevice = item, item => item is not null);
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<EventViewModel> RecentEvents { get; } = [];
    public ObservableCollection<EventViewModel> FilteredEvents { get; } = [];
    public ObservableCollection<EventViewModel> SelectedDeviceLogs { get; } = [];
    public ObservableCollection<EventViewModel> SelectedDeviceChanges { get; } = [];
    public ObservableCollection<DeviceMetricDto> CollectorHealth { get; } = [];
    public ObservableCollection<OperationalStatusDto> OperationalStatuses { get; } = [];
    public IReadOnlyList<string> RegisteredChecks { get; } = ["interface_status", "system", "log_ram", "version"];
    public IReadOnlyList<EventFilterOption> EventFilters { get; }

    public ICommand RefreshCommand { get; }
    public ICommand ManualCheckCommand { get; }
    public ICommand AcknowledgeCommand { get; }
    public ICommand SelectDeviceCommand { get; }

    public event EventHandler<EventViewModel>? AlertRaised;
    public ViewerSettings CurrentSettings => _settings;
    public long AppliedChangeCursor => Interlocked.Read(ref _changeCursor);

    public DeviceViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                RebuildSelectedDeviceEvents();
                OnPropertyChanged(nameof(HasSelectedDevice));
                NotifyCommandStates();
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
                OnPropertyChanged(nameof(MiniCurrentStatusText));
                OnPropertyChanged(nameof(MiniIssueTitle));
                OnPropertyChanged(nameof(MiniIssueDetail));
                NotifyCommandStates();
            }
        }
    }

    public AgentConnectionState HttpConnectionState
    {
        get => _httpConnectionState;
        private set
        {
            if (SetProperty(ref _httpConnectionState, value))
            {
                OnPropertyChanged(nameof(HttpConnectionText));
                UpdateCombinedConnectionState();
                if (_hasSnapshot && _lastRefreshedAt is { } generatedAt) RebuildCollectorHealth(generatedAt);
            }
        }
    }

    public AgentConnectionState RealtimeConnectionState
    {
        get => _realtimeConnectionState;
        private set
        {
            if (SetProperty(ref _realtimeConnectionState, value))
            {
                OnPropertyChanged(nameof(RealtimeConnectionText));
                UpdateCombinedConnectionState();
                if (_hasSnapshot && _lastRefreshedAt is { } generatedAt) RebuildCollectorHealth(generatedAt);
            }
        }
    }

    public string ConnectionText => ConnectionState switch
    {
        AgentConnectionState.Connected => "Agent 연결됨",
        AgentConnectionState.Demo => "데모 모드",
        AgentConnectionState.Connecting => "연결 중",
        AgentConnectionState.Reconnecting => "실시간 재연결 중",
        AgentConnectionState.Stale => "현재 미확인",
        AgentConnectionState.NeedsConnection => "연결 설정 필요",
        _ => "Agent 오프라인"
    };

    public DeviceHealth ConnectionHealth => ConnectionState switch
    {
        AgentConnectionState.Connected or AgentConnectionState.Demo => DeviceHealth.Normal,
        AgentConnectionState.Connecting => DeviceHealth.Loading,
        AgentConnectionState.Reconnecting => DeviceHealth.Warning,
        AgentConnectionState.Stale => DeviceHealth.Warning,
        _ => DeviceHealth.Disconnected
    };

    public string HttpConnectionText => HttpConnectionState switch
    {
        AgentConnectionState.Connected => "HTTP 상태 수신 정상",
        AgentConnectionState.Demo => "데모 상태 수신",
        AgentConnectionState.Stale => "HTTP 상태 준비 안 됨",
        AgentConnectionState.NeedsConnection => "HTTP 연결 설정 필요",
        AgentConnectionState.Connecting => "HTTP 연결 중",
        _ => "HTTP 상태 수신 실패"
    };

    public string RealtimeConnectionText => RealtimeConnectionState switch
    {
        AgentConnectionState.Connected => "실시간 이벤트 연결됨",
        AgentConnectionState.Demo => "데모 이벤트 연결됨",
        AgentConnectionState.Reconnecting => "실시간 이벤트 재연결 중",
        AgentConnectionState.Connecting => "실시간 이벤트 연결 중",
        AgentConnectionState.NeedsConnection => "실시간 이벤트 연결 설정 필요",
        _ => "실시간 이벤트 연결 끊김"
    };

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(BusyVisibility));
                NotifyCommandStates();
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
        : $"마지막 상태 생성 {_lastRefreshedAt.Value.LocalDateTime:MM-dd HH:mm:ss}";

    public DateTimeOffset? LastSuccessfulReceiptAt => _lastSuccessfulReceiptAt;
    public string LastSuccessfulReceiptText => _lastSuccessfulReceiptAt is null
        ? "성공 수신 없음"
        : $"마지막 성공 수신 {_lastSuccessfulReceiptAt.Value.LocalDateTime:MM-dd HH:mm:ss}";

    public string SelectedCheckId
    {
        get => _selectedCheckId;
        set => SetProperty(ref _selectedCheckId, value);
    }

    public string EventSearchText
    {
        get => _eventSearchText;
        set
        {
            if (SetProperty(ref _eventSearchText, value ?? string.Empty)) RebuildFilteredEvents();
        }
    }

    public EventFilterOption SelectedEventFilter
    {
        get => _selectedEventFilter;
        set
        {
            if (value is not null && SetProperty(ref _selectedEventFilter, value)) RebuildFilteredEvents();
        }
    }

    public int TotalCount => HealthSummary.Total;
    public int NormalCount => HealthSummary.Normal;
    public int WarningCount => HealthSummary.Warning;
    public int CriticalCount => HealthSummary.Critical;
    public int DisconnectedCount => HealthSummary.Disconnected;
    public int CriticalDisplayCount => CriticalCount + DisconnectedCount;
    public int NewLogCount => RecentEvents.Count(item => !item.Acknowledged && IsLogEvent(item));
    public int RecoveredCount => RecentEvents.Count(item => item.Recovered);
    public int UnacknowledgedCount => (int)Math.Clamp(
        _authoritativeUnacknowledged ?? RecentEvents.LongCount(item => !item.Acknowledged),
        0,
        int.MaxValue);
    public long? AuthoritativeUnacknowledgedCount => _authoritativeUnacknowledged;
    public int VisibleEventCount => FilteredEvents.Count;
    public string EventCountText => $"미확인 {UnacknowledgedCount:N0} · 표시 {VisibleEventCount:N0}";
    public string ApiVersionText => $"API v{_apiVersion}";
    public string MiniCurrentStatusText => ConnectionState is AgentConnectionState.Connected or AgentConnectionState.Demo
        ? "현재 상태 확인됨"
        : "현재 상태 미확인";
    public string MiniIssueTitle => ConnectionState switch
    {
        AgentConnectionState.NeedsConnection => "Viewer 연결 설정 필요",
        AgentConnectionState.Offline => "원격 수집기 연결 끊김",
        AgentConnectionState.Reconnecting => "실시간 이벤트 재연결 중",
        AgentConnectionState.Stale => "원격 상태 수신 지연",
        _ when CriticalDisplayCount > 0 => $"장애 장비 {CriticalDisplayCount}대",
        _ when WarningCount > 0 => $"경고 장비 {WarningCount}대",
        _ => "모든 장비 정상"
    };
    public string MiniIssueDetail => ConnectionState is AgentConnectionState.Connected or AgentConnectionState.Demo
        ? $"새 로그 {NewLogCount}건 · 미확인 {UnacknowledgedCount}건"
        : "정상 캐시는 유지 · 신규 상태 수신 안 됨";
    public DeviceHealthSummary HealthSummary => StatusAggregator.Aggregate(Devices);

    public bool NavigateToEvent(string? eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId) || !_eventsById.TryGetValue(eventId, out var item)) return false;
        EventSearchText = string.Empty;
        SelectedEventFilter = EventFilters[0];
        var device = Devices.FirstOrDefault(candidate => candidate.Id == item.DeviceId);
        if (device is not null) SelectedDevice = device;
        SelectedEvent = item;
        return true;
    }

    public void ReportOperation(string message) => OperationMessage = message;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        cancellationToken = linked.Token;
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized || _disposed) return;
            await RunOnUiAsync(() =>
            {
                IsBusy = true;
                OperationMessage = "Agent 상태와 변경 이력을 동기화하는 중입니다.";
            }).ConfigureAwait(false);

            if (_client is UnavailableAgentClient)
            {
                await RunOnUiAsync(() =>
                {
                    HttpConnectionState = AgentConnectionState.NeedsConnection;
                    RealtimeConnectionState = AgentConnectionState.NeedsConnection;
                    OperationMessage = "Agent 주소와 포트를 설정해 주세요.";
                }).ConfigureAwait(false);
                _initialized = true;
                return;
            }

            AgentSnapshotDto? snapshot = null;
            var hadPersistedCursor = false;
            try
            {
                snapshot = await _client.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                hadPersistedCursor = ApplyCursorIdentity(snapshot);
                await RunOnUiAsync(() => ApplySnapshotCore(snapshot)).ConfigureAwait(false);
                var recent = await _client.GetRecentEventsAsync(EventPageSize, cancellationToken).ConfigureAwait(false);
                await RunOnUiAsync(() => ApplyRecentEventsCore(recent)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await SetUnavailableStateAsync(exception, AgentChannel.Http).ConfigureAwait(false);
            }

            var hubStarted = false;
            try
            {
                await _client.StartAsync(cancellationToken).ConfigureAwait(false);
                hubStarted = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await SetUnavailableStateAsync(exception, AgentChannel.Realtime).ConfigureAwait(false);
            }

            var changeFeedSynchronized = false;
            if (snapshot is not null)
            {
                try
                {
                    await SynchronizeChangesAsync(hadPersistedCursor, cancellationToken).ConfigureAwait(false);
                    changeFeedSynchronized = true;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await SetUnavailableStateAsync(exception, AgentChannel.Http).ConfigureAwait(false);
                }
            }

            _allowLiveAlerts = true;
            _initialized = true;
            if (!_disposed && !_lifetime.IsCancellationRequested)
            {
                _snapshotLoop = Task.Run(() => SnapshotLoopAsync(_lifetime.Token));
            }
            if (snapshot is not null && hubStarted && changeFeedSynchronized &&
                Interlocked.Read(ref _feedResetCount) == 0)
            {
                await RunOnUiAsync(() =>
                {
                    OperationMessage = _settings.DemoMode ? "오프라인 데모가 실행 중입니다." : "실시간 모니터링 중입니다.";
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            await RunOnUiAsync(() => IsBusy = false).ConfigureAwait(false);
            _initializeGate.Release();
        }
    }

    public void ApplySnapshot(AgentSnapshotDto snapshot) => RunOnUi(() => ApplySnapshotCore(snapshot));

    public void ApplyEvents(IEnumerable<SwitchEventDto> events, bool raiseAlerts = true) =>
        RunOnUi(() =>
        {
            foreach (var item in events.OrderBy(item => item.Sequence)) UpsertEvent(item, raiseAlerts, "Created");
            RebuildSelectedDeviceEvents();
            NotifySummaryChanged();
        });

    public async Task SynchronizeChangesAsync(bool raiseCatchupSummary = false, CancellationToken cancellationToken = default)
    {
        if (_client is UnavailableAgentClient || _disposed) return;
        var client = _client;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _syncGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(client, _client)) return;
            await SynchronizeChangesCoreAsync(client, raiseCatchupSummary, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task SynchronizeChangesCoreAsync(
        IAgentClient client,
        bool raiseCatchupSummary,
        CancellationToken cancellationToken)
    {
        var catchupCandidates = raiseCatchupSummary ? new List<AlertCandidate>() : null;
        var feedResetBefore = Interlocked.Read(ref _feedResetCount);
        await DrainBufferedChangesAsync(catchupCandidates, cancellationToken).ConfigureAwait(false);
        long target = -1;
        var pageCount = 0;
        var completed = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (++pageCount > 10_000) throw new InvalidDataException("EVENT_CHANGE_PAGE_LIMIT");
            var before = AppliedChangeCursor;
            var page = await client.GetEventChangesAsync(before, EventPageSize, cancellationToken).ConfigureAwait(false);
            if (page.ResetRequired)
            {
                await RebaselineEventFeedAsync(client, page.ResetCursor, cancellationToken).ConfigureAwait(false);
                target = -1;
                continue;
            }
            if (target < 0) target = Math.Max(before, page.HighWatermark);
            BufferChanges(page.Changes, live: false);
            await DrainBufferedChangesAsync(catchupCandidates, cancellationToken).ConfigureAwait(false);
            var after = AppliedChangeCursor;

            if (after >= target && !page.HasMore)
            {
                completed = true;
                break;
            }
            if (after == before)
            {
                await RunOnUiAsync(() =>
                {
                    HttpConnectionState = _hasSnapshot ? AgentConnectionState.Stale : AgentConnectionState.Offline;
                    OperationMessage = "이벤트 변경 순서에 빈 구간이 있어 다음 동기화를 기다립니다. · EVENT_CHANGE_GAP";
                }).ConfigureAwait(false);
                break;
            }
        }

        await DrainBufferedChangesAsync(catchupCandidates, cancellationToken).ConfigureAwait(false);
        if (completed && catchupCandidates is { Count: > 0 }
            && Interlocked.Read(ref _feedResetCount) == feedResetBefore)
        {
            await RunOnUiAsync(() => RaiseCatchupSummary(catchupCandidates)).ConfigureAwait(false);
        }
    }

    public async Task SwitchClientAsync(ViewerSettings settings, CancellationToken cancellationToken = default)
    {
        await _initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var clean = ViewerSettingsSanitizer.Sanitize(settings);
            if (!clean.DemoMode && !ViewerSettingsSanitizer.IsValidForLiveConnection(clean, out var reason))
            {
                throw new InvalidOperationException(reason);
            }

            var replacement = _clientFactory.Create(clean);
            try
            {
                var snapshot = await replacement.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                var recent = await replacement.GetRecentEventsAsync(EventPageSize, cancellationToken).ConfigureAwait(false);
                await replacement.StartAsync(cancellationToken).ConfigureAwait(false);

                var hasCursor = clean.TryGetEventCursor(snapshot.AgentId, out var replacementCursor);
                if (!hasCursor)
                {
                    replacementCursor = snapshot.HighWatermark;
                    clean.SetEventCursor(snapshot.AgentId, replacementCursor);
                }
                _ = await replacement.GetEventChangesAsync(replacementCursor, 1, cancellationToken).ConfigureAwait(false);
                await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                IAgentClient previous;
                try
                {
                    _settingsStore.Save(clean);
                    previous = _client;
                    UnsubscribeClient(previous);
                    _client = replacement;
                    _settings = clean;
                    _currentAgentId = snapshot.AgentId;
                    Interlocked.Exchange(ref _changeCursor, replacementCursor);
                    lock (_changeSync)
                    {
                        _changeBuffer.Clear();
                        _liveAlertSequences.Clear();
                    }
                    SubscribeClient(replacement);
                    _allowLiveAlerts = false;
                    await RunOnUiAsync(() =>
                    {
                        Devices.Clear();
                        RecentEvents.Clear();
                        _eventsById.Clear();
                        SelectedDevice = null;
                        ApplySnapshotCore(snapshot);
                        RealtimeConnectionState = snapshot.ConnectionState == AgentConnectionState.Demo
                            ? AgentConnectionState.Demo
                            : AgentConnectionState.Connected;
                        ApplyRecentEventsCore(recent);
                    }).ConfigureAwait(false);
                }
                finally
                {
                    _syncGate.Release();
                }

                try
                {
                    await SynchronizeChangesAsync(hasCursor, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The replacement already passed snapshot, recent-event, hub,
                    // and change-feed preflight. A later network interruption makes
                    // the new connection stale; it must not resurrect old settings.
                    await SetUnavailableStateAsync(exception, AgentChannel.Http).ConfigureAwait(false);
                }
                finally
                {
                    _allowLiveAlerts = true;
                    if ((_snapshotLoop is null || _snapshotLoop.IsCompleted) && !_lifetime.IsCancellationRequested)
                    {
                        _snapshotLoop = Task.Run(() => SnapshotLoopAsync(_lifetime.Token));
                    }
                    if (!ReferenceEquals(previous, replacement))
                    {
                        try { await previous.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException) { }
                    }
                }
            }
            catch
            {
                if (!ReferenceEquals(replacement, _client)) await replacement.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    private IAgentClient CreateInitialClient(ViewerSettings settings)
    {
        if (!settings.DemoMode && !ViewerSettingsSanitizer.IsValidForLiveConnection(settings, out _))
        {
            HttpConnectionState = AgentConnectionState.NeedsConnection;
            RealtimeConnectionState = AgentConnectionState.NeedsConnection;
            return new UnavailableAgentClient();
        }

        try { return _clientFactory.Create(settings); }
        catch (InvalidOperationException)
        {
            HttpConnectionState = AgentConnectionState.NeedsConnection;
            RealtimeConnectionState = AgentConnectionState.NeedsConnection;
            return new UnavailableAgentClient();
        }
    }

    private bool ApplyCursorIdentity(AgentSnapshotDto snapshot)
    {
        if (!string.Equals(_currentAgentId, snapshot.AgentId, StringComparison.Ordinal))
        {
            lock (_changeSync)
            {
                _changeBuffer.Clear();
                _liveAlertSequences.Clear();
            }
        }
        _currentAgentId = snapshot.AgentId;
        long cursor;
        lock (_settingsSync)
        {
            var hadPersistedCursor = _settings.TryGetEventCursor(snapshot.AgentId, out cursor);
            if (!hadPersistedCursor)
            {
                // LastEventSequence belonged to the v1 event stream and is not
                // compatible with the v2 append-only change sequence.
                cursor = snapshot.HighWatermark;
                _settings.SetEventCursor(snapshot.AgentId, cursor);
            }
            Interlocked.Exchange(ref _changeCursor, cursor);
            ScheduleSettingsSave();
            return hadPersistedCursor;
        }
    }

    private async Task SnapshotLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(SnapshotInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                _ = await RefreshSnapshotAndChangesAsync(true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task RefreshAsync()
    {
        var feedResetBefore = Interlocked.Read(ref _feedResetCount);
        await RunOnUiAsync(() =>
        {
            IsBusy = true;
            OperationMessage = "최신 상태를 불러오는 중입니다.";
        }).ConfigureAwait(false);
        try
        {
            if (await RefreshSnapshotAndChangesAsync(true, _lifetime.Token).ConfigureAwait(false)
                && Interlocked.Read(ref _feedResetCount) == feedResetBefore)
            {
                await RunOnUiAsync(() => OperationMessage = "최신 상태로 갱신했습니다.").ConfigureAwait(false);
            }
        }
        finally
        {
            await RunOnUiAsync(() => IsBusy = false).ConfigureAwait(false);
        }
    }

    private async Task<bool> RefreshSnapshotAndChangesAsync(bool raiseCatchupSummary, CancellationToken cancellationToken)
    {
        var client = _client;
        try
        {
            var snapshot = await client.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            await _syncGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(client, _client)) return false;
                if (_lastRefreshedAt.HasValue && snapshot.GeneratedAt < _lastRefreshedAt.Value)
                {
                    return false;
                }
                var identityChanged = !string.Equals(snapshot.AgentId, _currentAgentId, StringComparison.Ordinal);
                if (identityChanged) ApplyCursorIdentity(snapshot);
                await RunOnUiAsync(() => ApplySnapshotCore(snapshot)).ConfigureAwait(false);
                // The recent snapshot and the following change-feed catch-up
                // form one critical section. A live ACK/recovery cannot advance
                // the cursor and then be overwritten by an older recent page.
                var recent = await client.GetRecentEventsAsync(EventPageSize, linked.Token).ConfigureAwait(false);
                if (!ReferenceEquals(client, _client)) return false;
                await RunOnUiAsync(() =>
                {
                    RecentEvents.Clear();
                    _eventsById.Clear();
                    ApplyRecentEventsCore(recent);
                }).ConfigureAwait(false);
                await SynchronizeChangesCoreAsync(client, raiseCatchupSummary, linked.Token).ConfigureAwait(false);
            }
            finally
            {
                _syncGate.Release();
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception exception)
        {
            await SetUnavailableStateAsync(exception, AgentChannel.Http).ConfigureAwait(false);
            return false;
        }
    }

    private async Task ExecuteManualCheckAsync()
    {
        var device = SelectedDevice;
        if (device is null) return;
        await RunOnUiAsync(() =>
        {
            IsBusy = true;
            OperationMessage = $"{device.Name} · {SelectedCheckId} 점검 요청 중";
        }).ConfigureAwait(false);
        try
        {
            var result = await _client.ExecuteRegisteredCheckAsync(device.Id, SelectedCheckId, _lifetime.Token).ConfigureAwait(false);
            await RunOnUiAsync(() => OperationMessage = result.Accepted ? result.Message : $"점검 거부 · {result.ErrorCode ?? "UNKNOWN"}").ConfigureAwait(false);
            if (result.Accepted) _ = await RefreshSnapshotAndChangesAsync(true, _lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RunOnUiAsync(() => OperationMessage = $"점검 요청 실패 · {SafeMessage(exception)}").ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiAsync(() => IsBusy = false).ConfigureAwait(false);
        }
    }

    private async Task AcknowledgeAsync(EventViewModel? item)
    {
        if (item is null || item.Acknowledged) return;
        try
        {
            if (await _client.AcknowledgeAsync(item.AgentEventId, _lifetime.Token).ConfigureAwait(false))
            {
                await RunOnUiAsync(() =>
                {
                    item.Acknowledged = true;
                    RebuildFilteredEvents();
                    NotifySummaryChanged();
                }).ConfigureAwait(false);
                await SynchronizeChangesAsync(false, _lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await RunOnUiAsync(() => OperationMessage = $"확인 처리 실패 · {SafeMessage(exception)}").ConfigureAwait(false);
        }
    }

    private void BufferChanges(IEnumerable<AgentEventChangeDto> changes, bool live)
    {
        lock (_changeSync)
        {
            foreach (var change in changes)
            {
                if (change.ChangeSequence <= AppliedChangeCursor) continue;
                _changeBuffer[change.ChangeSequence] = change;
                if (live && _allowLiveAlerts) _liveAlertSequences.Add(change.ChangeSequence);
            }
        }
    }

    private async Task DrainBufferedChangesAsync(
        List<AlertCandidate>? catchupCandidates,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            AgentEventChangeDto? change;
            bool liveAlert;
            lock (_changeSync)
            {
                var next = AppliedChangeCursor + 1;
                if (!_changeBuffer.Remove(next, out change)) return;
                liveAlert = _liveAlertSequences.Remove(next);
            }

            await RunOnUiAsync(() => ApplyEventChangeCore(change, liveAlert)).ConfigureAwait(false);
            if (!liveAlert && catchupCandidates is not null && IsNotifiableChange(change))
            {
                catchupCandidates.Add(new AlertCandidate(change.ChangeSequence, change.ChangeKind, change.Event));
            }
            Interlocked.Exchange(ref _changeCursor, change.ChangeSequence);
            lock (_settingsSync) _settings.SetEventCursor(_currentAgentId, change.ChangeSequence);
            ScheduleSettingsSave();
        }
    }

    private async Task RebaselineEventFeedAsync(
        IAgentClient client,
        long resetCursor,
        CancellationToken cancellationToken)
    {
        var recent = await client.GetRecentEventsAsync(EventPageSize, cancellationToken).ConfigureAwait(false);
        lock (_changeSync)
        {
            _changeBuffer.Clear();
            _liveAlertSequences.Clear();
        }
        var safeCursor = Math.Max(0, resetCursor);
        Interlocked.Exchange(ref _changeCursor, safeCursor);
        lock (_settingsSync) _settings.SetEventCursor(_currentAgentId, safeCursor);
        Interlocked.Increment(ref _feedResetCount);
        ScheduleSettingsSave();
        await RunOnUiAsync(() =>
        {
            RecentEvents.Clear();
            _eventsById.Clear();
            ApplyRecentEventsCore(recent);
            OperationMessage = "보존 기간이 지난 이벤트 구간을 현재 상태로 다시 맞췄습니다. · EVENT_FEED_RESET";
        }).ConfigureAwait(false);
    }

    private void ApplyEventChangeCore(AgentEventChangeDto change, bool raiseAlert)
    {
        UpsertEvent(change.Event, raiseAlert, change.ChangeKind);
        RebuildSelectedDeviceEvents();
        NotifySummaryChanged();
    }

    private void ApplyRecentEventsCore(IEnumerable<SwitchEventDto> events)
    {
        foreach (var item in events.OrderBy(item => item.Sequence)) UpsertEvent(item, false, "Recent");
        RebuildSelectedDeviceEvents();
        RebuildFilteredEvents();
        if (_hasSnapshot && _lastRefreshedAt is { } generatedAt) RebuildCollectorHealth(generatedAt);
        NotifySummaryChanged();
    }

    private void UpsertEvent(SwitchEventDto source, bool raiseAlert, string changeKind)
    {
        if (string.IsNullOrWhiteSpace(source.AgentEventId)) return;
        if (source.Recovered)
        {
            source = source with
            {
                Acknowledged = true,
                RecoveredAt = source.RecoveredAt ?? DateTimeOffset.Now
            };
        }
        if (_eventsById.TryGetValue(source.AgentEventId, out var existing))
        {
            var wasRecovered = existing.Recovered;
            existing.Update(source);
            if (raiseAlert && source.Recovered && !wasRecovered)
            {
                AlertRaised?.Invoke(this, existing);
            }
            RebuildFilteredEvents();
            return;
        }

        var item = new EventViewModel(source);
        _eventsById[item.AgentEventId] = item;
        var insertAt = 0;
        while (insertAt < RecentEvents.Count && CompareEvents(RecentEvents[insertAt], item) > 0) insertAt++;
        RecentEvents.Insert(insertAt, item);
        while (RecentEvents.Count > EventPageSize)
        {
            var removed = RecentEvents[^1];
            _eventsById.Remove(removed.AgentEventId);
            RecentEvents.RemoveAt(RecentEvents.Count - 1);
        }

        if (raiseAlert &&
            (source.Recovered
             || (changeKind.Equals("Created", StringComparison.OrdinalIgnoreCase)
                 && !item.Acknowledged && !item.Recovered
                 && item.Severity is DeviceHealth.Warning or DeviceHealth.Critical or DeviceHealth.Disconnected)))
        {
            AlertRaised?.Invoke(this, item);
        }
        RebuildFilteredEvents();
    }

    private static bool IsNotifiableChange(AgentEventChangeDto change) =>
        change.Event.Recovered || change.ChangeKind.Equals("Recovered", StringComparison.OrdinalIgnoreCase)
        || (change.ChangeKind.Equals("Created", StringComparison.OrdinalIgnoreCase)
            && !change.Event.Acknowledged
            && change.Event.Severity is DeviceHealth.Warning or DeviceHealth.Critical or DeviceHealth.Disconnected);

    private void RaiseCatchupSummary(IReadOnlyList<AlertCandidate> candidates)
    {
        var ranked = candidates
            .OrderByDescending(candidate => AlertPriority(candidate.Event))
            .ThenByDescending(candidate => candidate.ChangeSequence)
            .ToArray();
        var highest = ranked.FirstOrDefault(candidate => _eventsById.ContainsKey(candidate.Event.AgentEventId))
                      ?? ranked[0];
        var activeCritical = candidates.Select(candidate => candidate.Event.AgentEventId)
            .Distinct(StringComparer.Ordinal)
            .Count(eventId => _eventsById.TryGetValue(eventId, out var current)
                              && !current.Recovered
                              && current.Severity is DeviceHealth.Critical or DeviceHealth.Disconnected);
        var detail = activeCritical > 0
            ? $"최고 심각도 {HealthText(highest.Event.Severity)} · 활성 장애 {activeCritical}건"
            : $"최고 심각도 {HealthText(highest.Event.Severity)} · 최근 상태를 확인하세요.";
        var summary = new EventViewModel(new SwitchEventDto(
            highest.Event.Sequence,
            $"viewer-catchup-{_currentAgentId}-{AppliedChangeCursor}",
            highest.Event.DeviceId,
            highest.Event.DeviceName,
            DateTimeOffset.Now,
            highest.Event.Severity,
            "동기화 요약",
            $"놓친 변경 {candidates.Count}건 동기화",
            detail,
            Acknowledged: true,
            Recovered: candidates.All(candidate => candidate.Event.Recovered),
            ConditionKey: $"viewer-catchup-{AppliedChangeCursor}",
            NavigationEventId: highest.Event.AgentEventId));
        AlertRaised?.Invoke(this, summary);
    }

    private static int AlertPriority(SwitchEventDto item) => item.Recovered ? 0 : item.Severity switch
    {
        DeviceHealth.Critical => 4,
        DeviceHealth.Disconnected => 3,
        DeviceHealth.Warning => 2,
        _ => 1
    };

    private static string HealthText(DeviceHealth health) => health switch
    {
        DeviceHealth.Critical => "장애",
        DeviceHealth.Disconnected => "연결 끊김",
        DeviceHealth.Warning => "경고",
        _ => "복구"
    };

    private static int CompareEvents(EventViewModel left, EventViewModel right)
    {
        var occurred = left.OccurredAt.CompareTo(right.OccurredAt);
        return occurred != 0 ? occurred : left.Sequence.CompareTo(right.Sequence);
    }

    private void ApplySnapshotCore(AgentSnapshotDto snapshot)
    {
        HttpConnectionState = snapshot.ConnectionState;
        CollectorVersion = snapshot.CollectorVersion;
        CollectorSummary = snapshot.CollectorSummary;
        _authoritativeUnacknowledged = snapshot.AuthoritativeUnacknowledged;
        _apiVersion = snapshot.ApiVersion;
        _snapshotOperationalStatuses = snapshot.OperationalStatuses ?? [];
        _lastRefreshedAt = snapshot.GeneratedAt;
        _lastSuccessfulReceiptAt = DateTimeOffset.Now;
        _hasSnapshot = true;
        OnPropertyChanged(nameof(LastRefreshText));
        OnPropertyChanged(nameof(LastSuccessfulReceiptAt));
        OnPropertyChanged(nameof(LastSuccessfulReceiptText));

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
    }

    private void OnClientEventChanged(object? sender, AgentEventChangeDto item)
    {
        if (sender is not IAgentClient client) return;
        _ = ProcessClientEventAsync(client, item);
    }

    private async Task ProcessClientEventAsync(IAgentClient client, AgentEventChangeDto item)
    {
        try
        {
            await _syncGate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                if (!ReferenceEquals(client, _client)) return;
                BufferChanges([item], live: true);
                await SynchronizeChangesCoreAsync(client, false, _lifetime.Token).ConfigureAwait(false);
            }
            finally
            {
                _syncGate.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { await SetUnavailableStateAsync(exception, AgentChannel.Http).ConfigureAwait(false); }
    }

    private void OnConnectionStateChanged(object? sender, AgentConnectionState state)
    {
        if (!ReferenceEquals(sender, _client)) return;
        RunOnUi(() =>
        {
            if (!ReferenceEquals(sender, _client)) return;
            RealtimeConnectionState = state;
        });
        if (state == AgentConnectionState.Connected && _initialized) _ = ReconnectCatchupAsync();
    }

    private async Task ReconnectCatchupAsync()
    {
        if (await RefreshSnapshotAndChangesAsync(true, _lifetime.Token).ConfigureAwait(false))
        {
            await RunOnUiAsync(() => OperationMessage = "재연결 후 누락된 변경을 동기화했습니다.").ConfigureAwait(false);
        }
    }

    private void SubscribeClient(IAgentClient client)
    {
        client.EventChanged += OnClientEventChanged;
        client.ConnectionStateChanged += OnConnectionStateChanged;
    }

    private void UnsubscribeClient(IAgentClient client)
    {
        client.EventChanged -= OnClientEventChanged;
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

    private void RebuildFilteredEvents()
    {
        var query = EventSearchText.Trim();
        FilteredEvents.Clear();
        foreach (var item in RecentEvents)
        {
            var filterMatch = SelectedEventFilter.Value switch
            {
                EventFilter.Unacknowledged => !item.Acknowledged,
                EventFilter.NewLog => !item.Acknowledged && item.IsLogEvent,
                EventFilter.Critical => !item.Recovered
                                        && item.Severity is DeviceHealth.Critical or DeviceHealth.Disconnected,
                EventFilter.Recovered => item.Recovered,
                _ => true
            };
            if (!filterMatch) continue;
            if (query.Length > 0
                && !item.DeviceName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                && !item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                && !item.Detail.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                && !item.Kind.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            {
                continue;
            }
            FilteredEvents.Add(item);
        }
        OnPropertyChanged(nameof(VisibleEventCount));
        OnPropertyChanged(nameof(EventCountText));
    }

    private void RebuildCollectorHealth(DateTimeOffset generatedAt)
    {
        CollectorHealth.Clear();
        CollectorHealth.Add(new("Agent", ConnectionText, ConnectionHealth));
        CollectorHealth.Add(new("HTTP 상태", HttpConnectionText,
            HttpConnectionState is AgentConnectionState.Connected or AgentConnectionState.Demo ? DeviceHealth.Normal : DeviceHealth.Warning));
        CollectorHealth.Add(new("실시간 이벤트", RealtimeConnectionText,
            RealtimeConnectionState is AgentConnectionState.Connected or AgentConnectionState.Demo ? DeviceHealth.Normal : DeviceHealth.Warning));
        CollectorHealth.Add(new("마지막 성공 수신", LastSuccessfulReceiptText));
        CollectorHealth.Add(new("수집기 버전", CollectorVersion));
        CollectorHealth.Add(new("마지막 상태 생성", generatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")));
        CollectorHealth.Add(new("데이터 범위", "구조화 이벤트만 수신 · 원문 미수신"));

        OperationalStatuses.Clear();
        OperationalStatuses.Add(new OperationalStatusDto(
            ConnectionState is AgentConnectionState.Offline ? "AGENT_OFFLINE" : "AGENT_CHANNEL",
            "원격 수집기",
            ConnectionState is AgentConnectionState.Offline
                ? "Agent 연결 끊김 · 정상 캐시는 유지 · 신규 상태 미수신"
                : ConnectionText,
            ConnectionHealth));
        OperationalStatuses.Add(new OperationalStatusDto(
            "API_CHANNEL",
            "HTTP API",
            HttpConnectionText,
            HttpConnectionState is AgentConnectionState.Connected or AgentConnectionState.Demo
                ? DeviceHealth.Normal
                : DeviceHealth.Warning));
        OperationalStatuses.Add(new OperationalStatusDto(
            RealtimeConnectionState is AgentConnectionState.Connected or AgentConnectionState.Demo
                ? "REALTIME_AVAILABLE"
                : "REALTIME_DEGRADED",
            "SignalR 실시간",
            RealtimeConnectionState is AgentConnectionState.Connected or AgentConnectionState.Demo
                ? RealtimeConnectionText
                : $"{RealtimeConnectionText} · HTTP 캐치업 유지",
            RealtimeConnectionState is AgentConnectionState.Connected or AgentConnectionState.Demo
                ? DeviceHealth.Normal
                : DeviceHealth.Warning));

        foreach (var status in _snapshotOperationalStatuses.Where(status =>
                     status.Code is not "AGENT_CHANNEL" and not "API_CHANNEL"
                     && !status.Code.StartsWith("REALTIME_", StringComparison.Ordinal)))
        {
            if (OperationalStatuses.All(existing => !existing.Code.Equals(status.Code, StringComparison.Ordinal)))
            {
                OperationalStatuses.Add(status);
            }
        }

        if (OperationalStatuses.All(status =>
                !status.Code.Equals("HTTP_UNPROTECTED", StringComparison.Ordinal)))
        {
            OperationalStatuses.Add(new OperationalStatusDto(
                "HTTP_UNPROTECTED",
                "통신 보호",
                "사내 관리망 전용 · 암호화/인증 없음 · Windows 방화벽 허용 IPv4만 접근",
                DeviceHealth.Warning));
        }

        foreach (var device in Devices)
        {
            var unsupported = device.Capabilities.Where(item => !item.Supported).ToArray();
            if (unsupported.Length == 0) continue;
            OperationalStatuses.Add(new OperationalStatusDto(
                $"COLLECTOR_UNSUPPORTED_{device.Id}",
                $"{device.Name} 일부 수집 미지원",
                $"장비 상태는 유지 · {string.Join(", ", unsupported.Select(item => item.CommandId))}",
                DeviceHealth.Warning));
        }

        if (RecentEvents.Count == 0)
        {
            OperationalStatuses.Add(new OperationalStatusDto(
                "EMPTY",
                "변경 없음",
                "현재 필터 범위에 수신된 이벤트가 없습니다.",
                DeviceHealth.Normal));
        }
    }

    private async Task SetUnavailableStateAsync(Exception exception, AgentChannel channel)
    {
        await RunOnUiAsync(() =>
        {
            var typed = exception as AgentClientException;
            var state = typed?.SuggestedConnectionState ?? AgentConnectionState.Offline;
            if (state == AgentConnectionState.NeedsConnection)
            {
                HttpConnectionState = state;
                RealtimeConnectionState = state;
            }
            else if (channel == AgentChannel.Http)
            {
                HttpConnectionState = state;
            }
            else
            {
                RealtimeConnectionState = state;
            }
            OperationMessage = $"Agent 상태 미확인 · {SafeMessage(exception)}";
        }).ConfigureAwait(false);
    }

    private void UpdateCombinedConnectionState()
    {
        AgentConnectionState combined;
        if (HttpConnectionState == AgentConnectionState.NeedsConnection
            || RealtimeConnectionState == AgentConnectionState.NeedsConnection)
        {
            combined = AgentConnectionState.NeedsConnection;
        }
        else if (HttpConnectionState == AgentConnectionState.Demo
                 || RealtimeConnectionState == AgentConnectionState.Demo)
        {
            combined = AgentConnectionState.Demo;
        }
        else if (HttpConnectionState == AgentConnectionState.Offline)
        {
            combined = RealtimeConnectionState == AgentConnectionState.Connected && _hasSnapshot
                ? AgentConnectionState.Stale
                : AgentConnectionState.Offline;
        }
        else if (HttpConnectionState == AgentConnectionState.Stale
                 || RealtimeConnectionState == AgentConnectionState.Offline)
        {
            combined = AgentConnectionState.Stale;
        }
        else if (RealtimeConnectionState == AgentConnectionState.Reconnecting)
        {
            combined = AgentConnectionState.Reconnecting;
        }
        else if (RealtimeConnectionState == AgentConnectionState.Connecting)
        {
            combined = _initialized ? AgentConnectionState.Reconnecting : AgentConnectionState.Connecting;
        }
        else if (HttpConnectionState == AgentConnectionState.Connecting)
        {
            combined = AgentConnectionState.Connecting;
        }
        else
        {
            combined = AgentConnectionState.Connected;
        }
        ConnectionState = combined;
    }

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(HealthSummary));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(NormalCount));
        OnPropertyChanged(nameof(WarningCount));
        OnPropertyChanged(nameof(CriticalCount));
        OnPropertyChanged(nameof(DisconnectedCount));
        OnPropertyChanged(nameof(CriticalDisplayCount));
        OnPropertyChanged(nameof(NewLogCount));
        OnPropertyChanged(nameof(RecoveredCount));
        OnPropertyChanged(nameof(UnacknowledgedCount));
        OnPropertyChanged(nameof(AuthoritativeUnacknowledgedCount));
        OnPropertyChanged(nameof(VisibleEventCount));
        OnPropertyChanged(nameof(EventCountText));
        OnPropertyChanged(nameof(ApiVersionText));
        OnPropertyChanged(nameof(MiniCurrentStatusText));
        OnPropertyChanged(nameof(MiniIssueTitle));
        OnPropertyChanged(nameof(MiniIssueDetail));
    }

    private void NotifyCommandStates()
    {
        (RefreshCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (ManualCheckCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
    }

    private void RunOnUi(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext) action();
        else _uiContext.Post(_ => action(), null);
    }

    private Task RunOnUiAsync(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext)
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _uiContext.Post(_ =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }, null);
        return completion.Task;
    }

    private void ScheduleSettingsSave()
    {
        var generation = Interlocked.Increment(ref _settingsGeneration);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _lifetime.Token).ConfigureAwait(false);
                if (generation != Interlocked.Read(ref _settingsGeneration)) return;
                lock (_settingsSync) _settingsStore.Save(_settings);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch { }
        });
    }

    private static string SafeMessage(Exception exception) => exception switch
    {
        AgentClientException typed => typed.ErrorCode,
        HttpRequestException => "AGENT_UNREACHABLE",
        TaskCanceledException => "AGENT_TIMEOUT",
        InvalidOperationException invalid when invalid.Message.Contains("CONNECTION", StringComparison.OrdinalIgnoreCase) => "VIEWER_CONNECTION_REQUIRED",
        InvalidOperationException => "VIEWER_CONFIGURATION_INVALID",
        JsonException => "AGENT_RESPONSE_INVALID",
        _ => "VIEWER_UNEXPECTED_ERROR"
    };

    private static bool IsLogEvent(EventViewModel item) =>
        item.Kind.Contains("로그", StringComparison.Ordinal) || item.Kind.Contains("log", StringComparison.OrdinalIgnoreCase);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _allowLiveAlerts = false;
        _lifetime.Cancel();
        Interlocked.Increment(ref _settingsGeneration);
        UnsubscribeClient(_client);

        var initializeQuiesced = false;
        var syncQuiesced = false;
        try { initializeQuiesced = await _initializeGate.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        try { syncQuiesced = await _syncGate.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (ObjectDisposedException) { }

        if (_snapshotLoop is not null)
        {
            try { await _snapshotLoop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
        }

        try { await _client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
        try { lock (_settingsSync) _settingsStore.Save(_settings); } catch { }

        if (initializeQuiesced) _initializeGate.Dispose();
        if (syncQuiesced) _syncGate.Dispose();
        if (initializeQuiesced && syncQuiesced) _lifetime.Dispose();
    }
}
