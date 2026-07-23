using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using SamsungSwitchWatch.Core.Diagnostics;
using SamsungSwitchWatch.Core.Parsing;
using SamsungSwitchWatch.Core.Profiles;
using SamsungSwitchWatch.Viewer.Infrastructure;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.ViewModels;

public sealed record DeviceHealthSummary(
    int Total,
    int Normal,
    int Warning,
    int Critical,
    int Disconnected,
    int Loading,
    int Unmonitored)
{
    public int ProblemCount => Warning + Critical + Disconnected;

    public int Monitored => Total - Unmonitored;
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
            items.Count(item => item.Health == DeviceHealth.Loading),
            items.Count(item => item.Health == DeviceHealth.Empty));
    }
}

public sealed record EventFilterOption(EventFilter Value, string Label);
public sealed record RegisteredCheckOption(string Id, string DisplayName);

public sealed class DashboardViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly TimeSpan MonitoringFailureReportTimeout = TimeSpan.FromSeconds(1);

    private sealed record AlertCandidate(long ChangeSequence, string ChangeKind, SwitchEventDto Event);
    private sealed record MonitoringOutputAssessment(
        TelnetCommandOutputDto? Output,
        bool Ready,
        bool ExplicitlyUnsupported,
        string? ErrorCode);
    private enum AgentChannel { Http, Realtime }

    private const int EventPageSize = 500;
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(60);
    private static readonly DeviceProfileRegistry MonitoringProfiles = new(
    [
        Ies4028XpProfile.Create(),
        Ies4224GpProfile.Create(),
        Ies4226XpProfile.Create()
    ]);

    private readonly IAgentClientFactory _clientFactory;
    private readonly ViewerSettingsSaveCoordinator _settingsSaveCoordinator;
    private readonly ManagedDeviceStore? _deviceStore;
    private readonly ViewerMonitoringStore? _monitoringStore;
    private readonly Action<string, string> _writeDiagnostic;
    private readonly Func<TimeSpan, CancellationToken, Task> _settingsSaveDelay;
    private readonly SynchronizationContext? _uiContext;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _changeSync = new();
    private readonly object _settingsSync = new();
    private readonly object _monitoringCredentialBlockSync = new();
    private readonly object _deviceOperationGateSync = new();
    private readonly Dictionary<string, EventViewModel> _eventsById = new(StringComparer.Ordinal);
    private readonly HashSet<string> _monitoringCredentialBlocks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SemaphoreSlim> _deviceOperationGates =
        new(StringComparer.OrdinalIgnoreCase);
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
    private bool _readOnlyQueriesEnabled;
    private int _readOnlyQueryMaxCommandLength = 128;
    private int _readOnlyQueryMaxOutputBytes = 65_536;
    private string _readOnlyQueryCommand = "show port status";
    private string _readOnlyQueryOutput = string.Empty;
    private string _readOnlyQueryStatusText = "준비";
    private string _readOnlyQueryResultMeta = "실행 결과가 없습니다.";
    private bool _isReadOnlyQueryRunning;
    private bool _readOnlyQueryTruncated;
    private readonly List<string> _readOnlyQueryHistory = [];
    private int _readOnlyQueryHistoryIndex;
    private string _readOnlyQueryHistoryDraft = string.Empty;
    private bool _movingReadOnlyQueryHistory;
    private CancellationTokenSource? _readOnlyQueryCancellation;
    private long _readOnlyQueryContextGeneration;
    private IReadOnlyList<OperationalStatusDto> _snapshotOperationalStatuses = [];
    private long _changeCursor;
    private long _settingsGeneration;
    private long _feedResetCount;
    private bool _hasSnapshot;
    private bool _allowLiveAlerts;
    private bool _initialized;
    private bool _disposed;
    private bool _statelessV4;
    private ManagedDeviceLoadStatus _managedDeviceLoadStatus = ManagedDeviceLoadStatus.Missing;
    private IReadOnlyList<ManagedDeviceProfile> _lastManagedDeviceProfiles = [];
    private Task? _snapshotLoop;
    private Task? _monitorLoop;
    private readonly SemaphoreSlim _monitorGate = new(1, 1);
    private readonly SemaphoreSlim _monitorConcurrency = new(2, 2);

    public DashboardViewModel(
        ViewerSettings settings,
        ViewerSettingsStore settingsStore,
        IAgentClientFactory? clientFactory = null,
        SynchronizationContext? synchronizationContext = null,
        ManagedDeviceStore? deviceStore = null,
        ViewerMonitoringStore? monitoringStore = null)
        : this(
            settings,
            settingsStore,
            clientFactory,
            synchronizationContext,
            deviceStore,
            monitoringStore,
            new ViewerSettingsSaveCoordinator(settingsStore),
            null,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken))
    {
    }

    internal DashboardViewModel(
        ViewerSettings settings,
        ViewerSettingsStore settingsStore,
        IAgentClientFactory? clientFactory,
        SynchronizationContext? synchronizationContext,
        ManagedDeviceStore? deviceStore,
        ViewerMonitoringStore? monitoringStore,
        ViewerSettingsSaveCoordinator settingsSaveCoordinator,
        Action<string, string>? writeDiagnostic,
        Func<TimeSpan, CancellationToken, Task> settingsSaveDelay)
    {
        _settings = ViewerSettingsSanitizer.Sanitize(settings);
        _settingsSaveCoordinator = settingsSaveCoordinator
            ?? throw new ArgumentNullException(nameof(settingsSaveCoordinator));
        _deviceStore = deviceStore;
        _monitoringStore = monitoringStore;
        _writeDiagnostic = writeDiagnostic ?? ((_, _) => { });
        _settingsSaveDelay = settingsSaveDelay
            ?? throw new ArgumentNullException(nameof(settingsSaveDelay));
        _clientFactory = clientFactory ?? new AgentClientFactory();
        _uiContext = synchronizationContext ?? SynchronizationContext.Current;
        _client = CreateInitialClient(_settings);
        SubscribeClient(_client);

        EventFilters =
        [
            new(EventFilter.All, "전체"),
            new(EventFilter.Unacknowledged, "미확인 이벤트"),
            new(EventFilter.NewLog, "새 로그"),
            new(EventFilter.Critical, "장애"),
            new(EventFilter.Recovered, "복구")
        ];
        _selectedEventFilter = EventFilters[0];

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy && ConnectionState != AgentConnectionState.NeedsConnection);
        ManualCheckCommand = new AsyncRelayCommand(ExecuteManualCheckAsync, () => !IsBusy && SelectedDevice is not null && ConnectionState != AgentConnectionState.NeedsConnection);
        ExecuteReadOnlyQueryCommand = new AsyncRelayCommand(
            ExecuteReadOnlyQueryAsync,
            () => ReadOnlyQueriesEnabled
                  && !IsBusy
                  && !IsReadOnlyQueryRunning
                  && SelectedDevice is not null
                  && ConnectionState != AgentConnectionState.NeedsConnection
                  && !string.IsNullOrWhiteSpace(ReadOnlyQueryCommand));
        CancelReadOnlyQueryCommand = new RelayCommand(CancelReadOnlyQuery, () => IsReadOnlyQueryRunning);
        ClearReadOnlyQueryOutputCommand = new RelayCommand(ClearReadOnlyQueryOutput,
            () => !IsReadOnlyQueryRunning && (!string.IsNullOrEmpty(ReadOnlyQueryOutput) || ReadOnlyQueryStatusText != "준비"));
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
    public IReadOnlyList<RegisteredCheckOption> RegisteredChecks { get; } =
    [
        new("interface_status", "포트 상태"),
        new("system", "장비 상태"),
        new("log_ram", "시스템 로그"),
        new("version", "버전 정보")
    ];
    public IReadOnlyList<EventFilterOption> EventFilters { get; }

    public ICommand RefreshCommand { get; }
    public ICommand ManualCheckCommand { get; }
    public ICommand ExecuteReadOnlyQueryCommand { get; }
    public ICommand CancelReadOnlyQueryCommand { get; }
    public ICommand ClearReadOnlyQueryOutputCommand { get; }
    public ICommand AcknowledgeCommand { get; }
    public ICommand SelectDeviceCommand { get; }

    public event EventHandler<EventViewModel>? AlertRaised;
    public ViewerSettings CurrentSettings
    {
        get
        {
            lock (_settingsSync) return ViewerSettingsSanitizer.Copy(_settings);
        }
    }
    public long AppliedChangeCursor => Interlocked.Read(ref _changeCursor);
    internal int ReadOnlyQueryHistoryCount => _readOnlyQueryHistory.Count;
    public bool HasManagedDeviceStore => _deviceStore is not null;

    internal bool TryUpdateCurrentSettings(
        Action<ViewerSettings> update,
        string stage,
        out string errorCode)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_settingsSync)
        {
            _settings.Synchronize(update);
            return _settingsSaveCoordinator.TrySave(_settings, stage, out errorCode);
        }
    }

    public IReadOnlyList<ManagedDeviceProfile> GetManagedDevices()
    {
        if (_deviceStore is null)
        {
            return [];
        }

        var result = _deviceStore.LoadWithStatus();
        return result.Status switch
        {
            ManagedDeviceLoadStatus.Corrupt =>
                throw new InvalidDataException("VIEWER_DEVICE_STORE_CORRUPT"),
            ManagedDeviceLoadStatus.StorageUnavailable =>
                throw new IOException("VIEWER_DEVICE_STORE_UNAVAILABLE"),
            _ => result.Devices
        };
    }

    public ManagedDeviceDraft GetManagedDeviceDraft(string id) =>
        _deviceStore?.CreateEditDraft(id)
        ?? throw new InvalidOperationException("VIEWER_DEVICE_STORE_UNAVAILABLE");

    public ManagedDeviceProfile SaveManagedDevice(ManagedDeviceDraft draft) =>
        SaveManagedDevice(draft, out _);

    internal ManagedDeviceProfile SaveManagedDevice(
        ManagedDeviceDraft draft,
        out string? warningCode)
    {
        if (_deviceStore is null) throw new InvalidOperationException("VIEWER_DEVICE_STORE_UNAVAILABLE");
        var result = _deviceStore.Save(draft);
        warningCode = null;
        if (result.ConnectionVerified)
        {
            ClearMonitoringCredentialBlock(result.Id);
            warningCode = RunPostSaveCleanup(
                () => _monitoringStore?.ClearCapabilities(result.Id),
                warningCode);
        }
        if (!result.MonitoringEnabled)
        {
            warningCode = RunPostSaveCleanup(
                () => _monitoringStore?.ClearActiveFailure(result.Id),
                warningCode);
        }
        try
        {
            ReloadManagedDevices(result.Id);
        }
        catch
        {
            warningCode ??= "VIEWER_UNEXPECTED_ERROR";
        }
        if (warningCode is not null)
        {
            ReportDeviceManagementFailure("device-management-save", warningCode);
        }
        return result;
    }

    public bool DeleteManagedDevice(string id)
    {
        if (_deviceStore is null) return false;
        var removed = _deviceStore.Delete(id);
        if (removed)
        {
            ClearMonitoringCredentialBlock(id);
            ReloadManagedDevices();
        }
        return removed;
    }

    public ManagedDeviceProfile SetManagedDeviceMonitoring(string id, bool enabled)
    {
        if (_deviceStore is null) throw new InvalidOperationException("VIEWER_DEVICE_STORE_UNAVAILABLE");
        var result = _deviceStore.SetMonitoring(id, enabled);
        if (enabled) _monitoringStore?.ClearCapabilities(id);
        else _monitoringStore?.ClearActiveFailure(id);
        ReloadManagedDevices(id);
        return result;
    }

    public async Task<TelnetExecutionResultDto> TestManagedDeviceAsync(
        ManagedDeviceDraft draft,
        CancellationToken cancellationToken = default)
    {
        if (_deviceStore is null) throw new InvalidOperationException("VIEWER_DEVICE_STORE_UNAVAILABLE");
        var resolved = _deviceStore.ResolveDraftForOperation(draft);
        if (!ManagedDeviceValidator.TryValidate(resolved, true, out var reason))
        {
            throw new InvalidDataException(reason);
        }
        var request = new TelnetTargetDto(
            Guid.NewGuid().ToString("N"),
            resolved.Host.Trim(),
            23,
            resolved.Model.Trim(),
            resolved.Username.Trim(),
            resolved.Password,
            string.IsNullOrEmpty(resolved.EnablePassword) ? null : resolved.EnablePassword,
            "test");
        var operationGate = GetDeviceOperationGate(resolved.Host);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _client.TestTelnetAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.Success
                && !string.IsNullOrWhiteSpace(draft.Id)
                && _monitoringStore is not null)
            {
                var profile = _deviceStore.Load().FirstOrDefault(item =>
                    item.Id.Equals(draft.Id, StringComparison.Ordinal));
                if (profile is not null)
                {
                    var recoveries = _monitoringStore.RecordSuccess(profile);
                    if (recoveries.Count > 0) ApplyEvents(recoveries);
                    await RunOnUiAsync(() => UpdateManagedDevicePresentation(profile.Id)).ConfigureAwait(false);
                }
            }
            return result;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public void ReloadManagedDevices(string? preferredId = null)
    {
        if (_deviceStore is null) return;
        var loadResult = _deviceStore.LoadWithStatus();
        _managedDeviceLoadStatus = loadResult.Status;
        if (loadResult.Status is ManagedDeviceLoadStatus.Corrupt
            or ManagedDeviceLoadStatus.StorageUnavailable)
        {
            if (ManagedDeviceStoreWarning(loadResult.Status) is { } loadWarning)
            {
                OperationMessage = loadWarning;
            }
            return;
        }
        var profiles = loadResult.Devices;
        _lastManagedDeviceProfiles = profiles;
        var selectedId = preferredId ?? SelectedDevice?.Id;
        Devices.Clear();
        foreach (var snapshot in profiles
                     .Select(CreateManagedDeviceSnapshot)
                     .OrderBy(snapshot => DeviceDisplayPriority(snapshot.Health))
                     .ThenBy(snapshot => snapshot.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Devices.Add(new DeviceViewModel(snapshot));
        }
        SelectedDevice = Devices.FirstOrDefault(item => item.Id.Equals(selectedId, StringComparison.Ordinal))
                         ?? Devices.FirstOrDefault();
        NotifySummaryChanged();
        ReadOnlyQueriesEnabled = _statelessV4 && Devices.Count > 0;
    }

    private static string? RunPostSaveCleanup(
        Action action,
        string? currentWarning)
    {
        try
        {
            action();
            return currentWarning;
        }
        catch (Exception exception)
        {
            return currentWarning
                   ?? (IsMonitoringPersistenceFailure(exception)
                       ? "VIEWER_MONITOR_STATE_WRITE_FAILED"
                       : "VIEWER_UNEXPECTED_ERROR");
        }
    }

    public DeviceViewModel? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (SetProperty(ref _selectedDevice, value))
            {
                Interlocked.Increment(ref _readOnlyQueryContextGeneration);
                _readOnlyQueryCancellation?.Cancel();
                ReadOnlyQueryOutput = string.Empty;
                ReadOnlyQueryTruncated = false;
                ReadOnlyQueryStatusText = "준비";
                ReadOnlyQueryResultMeta = "실행 결과가 없습니다.";
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
                OnPropertyChanged(nameof(MiniIssueHealth));
                OnPropertyChanged(nameof(MiniCurrentStatusText));
                OnPropertyChanged(nameof(MiniIssueTitle));
                OnPropertyChanged(nameof(MiniIssueDetail));
                OnPropertyChanged(nameof(NormalSummaryCaption));
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

    public string HttpConnectionText => _statelessV4 && HttpConnectionState is AgentConnectionState.Connected or AgentConnectionState.Demo
        ? "HTTPS API 연결 정상"
        : HttpConnectionState switch
        {
            AgentConnectionState.Connected => "HTTP 상태 수신 정상",
            AgentConnectionState.Demo => "데모 상태 수신",
            AgentConnectionState.Stale => "HTTP 상태 준비 안 됨",
            AgentConnectionState.NeedsConnection => "HTTP 연결 설정 필요",
            AgentConnectionState.Connecting => "HTTP 연결 중",
            _ => "HTTP 상태 수신 실패"
        };

    public string RealtimeConnectionText => _statelessV4 && RealtimeConnectionState is AgentConnectionState.Connected or AgentConnectionState.Demo
        ? "Viewer 로컬 감시 준비"
        : RealtimeConnectionState switch
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
        set
        {
            if (SetProperty(ref _selectedCheckId, value)) OnPropertyChanged(nameof(SelectedCheckDisplayName));
        }
    }

    public string SelectedCheckDisplayName => RegisteredChecks
        .FirstOrDefault(item => item.Id.Equals(SelectedCheckId, StringComparison.Ordinal))?.DisplayName
        ?? SelectedCheckId;

    public bool ReadOnlyQueriesEnabled
    {
        get => _readOnlyQueriesEnabled;
        private set
        {
            if (SetProperty(ref _readOnlyQueriesEnabled, value))
            {
                OnPropertyChanged(nameof(ReadOnlyQueryUnavailableText));
                NotifyCommandStates();
            }
        }
    }

    public int ReadOnlyQueryMaxCommandLength
    {
        get => _readOnlyQueryMaxCommandLength;
        private set => SetProperty(ref _readOnlyQueryMaxCommandLength, Math.Clamp(value, 1, 4096));
    }

    public int ReadOnlyQueryMaxOutputBytes
    {
        get => _readOnlyQueryMaxOutputBytes;
        private set => SetProperty(ref _readOnlyQueryMaxOutputBytes, Math.Clamp(value, 1, 16 * 1024 * 1024));
    }

    public string ReadOnlyQueryCommand
    {
        get => _readOnlyQueryCommand;
        set
        {
            var clean = value ?? string.Empty;
            if (!SetProperty(ref _readOnlyQueryCommand, clean)) return;
            if (!_movingReadOnlyQueryHistory)
            {
                _readOnlyQueryHistoryIndex = _readOnlyQueryHistory.Count;
                _readOnlyQueryHistoryDraft = clean;
            }
            OnPropertyChanged(nameof(ReadOnlyQueryMayContainSensitiveData));
            NotifyCommandStates();
        }
    }

    public bool ReadOnlyQueryMayContainSensitiveData
    {
        get
        {
            var normalized = ReadOnlyQueryCommand.Trim();
            return normalized.StartsWith("show running-config", StringComparison.OrdinalIgnoreCase)
                   || normalized.StartsWith("show startup-config", StringComparison.OrdinalIgnoreCase)
                   || normalized.StartsWith("show configuration", StringComparison.OrdinalIgnoreCase)
                   || normalized.StartsWith("show tech-support", StringComparison.OrdinalIgnoreCase);
        }
    }

    public string ReadOnlyQueryOutput
    {
        get => _readOnlyQueryOutput;
        private set
        {
            if (SetProperty(ref _readOnlyQueryOutput, value))
            {
                OnPropertyChanged(nameof(HasReadOnlyQueryOutput));
                NotifyCommandStates();
            }
        }
    }

    public bool HasReadOnlyQueryOutput => !string.IsNullOrEmpty(ReadOnlyQueryOutput);

    public string ReadOnlyQueryStatusText
    {
        get => _readOnlyQueryStatusText;
        private set => SetProperty(ref _readOnlyQueryStatusText, value);
    }

    public string ReadOnlyQueryResultMeta
    {
        get => _readOnlyQueryResultMeta;
        private set => SetProperty(ref _readOnlyQueryResultMeta, value);
    }

    public bool ReadOnlyQueryTruncated
    {
        get => _readOnlyQueryTruncated;
        private set => SetProperty(ref _readOnlyQueryTruncated, value);
    }

    public bool IsReadOnlyQueryRunning
    {
        get => _isReadOnlyQueryRunning;
        private set
        {
            if (SetProperty(ref _isReadOnlyQueryRunning, value))
            {
                OnPropertyChanged(nameof(CanEditReadOnlyQuery));
                NotifyCommandStates();
            }
        }
    }

    public bool CanEditReadOnlyQuery => !IsReadOnlyQueryRunning;

    public string ReadOnlyQueryUnavailableText => !_hasSnapshot
        ? "Agent 기능 정보를 확인하는 중입니다."
        : _statelessV4 && Devices.Count == 0
        ? "장비 관리에서 스위치 IP와 계정을 먼저 등록해 주세요."
        : _apiVersion < 3
        ? "현재 Agent 버전은 장비 명령 기능을 지원하지 않습니다. Agent를 최신 버전으로 업데이트해 주세요."
        : "Agent에서 장비 명령 기능이 꺼져 있습니다. Agent 설치 또는 복구 설정에서 읽기 전용 명령을 사용하도록 설정해 주세요.";

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
    public int UnmonitoredCount => HealthSummary.Unmonitored;
    public int MonitoredCount => HealthSummary.Monitored;
    public int CriticalDisplayCount => CriticalCount + DisconnectedCount;
    public int NewLogCount => RecentEvents.Count(item => !item.Acknowledged && IsLogEvent(item));
    public int RecoveredCount => RecentEvents.Count(item => item.Recovered);
    public int UnacknowledgedCount => (int)Math.Clamp(
        _authoritativeUnacknowledged ?? RecentEvents.LongCount(item => !item.Acknowledged),
        0,
        int.MaxValue);
    public long? AuthoritativeUnacknowledgedCount => _authoritativeUnacknowledged;
    public int VisibleEventCount => FilteredEvents.Count;
    public string UnacknowledgedDisplayText => $"미확인 이벤트 {UnacknowledgedCount:N0}건";
    public string EventCountText => $"{UnacknowledgedDisplayText} · 표시 {VisibleEventCount:N0}건";
    public string ApiVersionText => $"API v{_apiVersion}";
    public string NormalSummaryCaption =>
        $"{(ConnectionState is AgentConnectionState.Connected or AgentConnectionState.Demo ? "현재 확인" : "마지막 확인")} 정상"
        + (UnmonitoredCount > 0 ? $" · 미감시 {UnmonitoredCount}대" : string.Empty);
    public string MiniCurrentStatusText => ConnectionState is AgentConnectionState.Connected or AgentConnectionState.Demo
        ? MonitoredCount == 0
            ? "Agent 연결됨 · 감시 대상 없음"
            : "현재 감시 상태 확인됨"
        : "현재 상태 미확인";
    public DeviceHealth MiniIssueHealth => ConnectionState switch
    {
        AgentConnectionState.NeedsConnection or AgentConnectionState.Offline => DeviceHealth.Disconnected,
        AgentConnectionState.Connecting => DeviceHealth.Loading,
        AgentConnectionState.Reconnecting or AgentConnectionState.Stale => DeviceHealth.Warning,
        _ when CriticalCount > 0 => DeviceHealth.Critical,
        _ when DisconnectedCount > 0 => DeviceHealth.Disconnected,
        _ when WarningCount > 0 => DeviceHealth.Warning,
        _ when MonitoredCount == 0 => DeviceHealth.Empty,
        _ => DeviceHealth.Normal
    };
    public string MiniIssueTitle => ConnectionState switch
    {
        AgentConnectionState.NeedsConnection => "연결 설정 필요",
        AgentConnectionState.Connecting => "원격 수집 PC 연결 중",
        AgentConnectionState.Offline => "원격 수집 PC 연결 끊김",
        AgentConnectionState.Reconnecting => "실시간 이벤트 재연결 중",
        AgentConnectionState.Stale => "원격 상태 수신 지연",
        _ when CriticalCount > 0 && DisconnectedCount > 0 => $"장애 {CriticalCount}대 · 접속 끊김 {DisconnectedCount}대",
        _ when CriticalCount > 0 => $"장애 장비 {CriticalCount}대",
        _ when DisconnectedCount > 0 => $"접속 끊김 장비 {DisconnectedCount}대",
        _ when WarningCount > 0 => $"경고 장비 {WarningCount}대",
        _ when TotalCount == 0 => "등록된 장비 없음",
        _ when MonitoredCount == 0 => "주기 감시 대상 없음",
        _ when UnacknowledgedCount > 0 => $"현재 감시 장비 정상 · 확인 대기 {UnacknowledgedCount}건",
        _ => "현재 감시 장비 정상"
    };
    public string MiniIssueDetail => ConnectionState switch
    {
        AgentConnectionState.Connected or AgentConnectionState.Demo when MonitoredCount == 0 =>
            "장비 관리에서 접속 시험 후 주기 감시를 켜세요.",
        AgentConnectionState.Connected or AgentConnectionState.Demo =>
            $"새 로그 {NewLogCount}건 · 미확인 이벤트 {UnacknowledgedCount}건",
        AgentConnectionState.Connecting => "첫 상태를 기다리는 중",
        AgentConnectionState.NeedsConnection => "연결 설정 후 상태를 확인할 수 있습니다.",
        _ when _hasSnapshot => "마지막 상태 유지 · 새 상태 수신 안 됨",
        _ => "수신된 상태 없음"
    };
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

    internal void ReportDeviceManagementFailure(string stage, string errorCode)
    {
        TryWriteDiagnostic(stage, errorCode);
        ReportOperation($"{ViewerConnectionMessages.ForCode(errorCode)} · {errorCode}");
    }

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

            if (_client.SupportsStatelessV4)
            {
                _statelessV4 = true;
                await RunOnUiAsync(() =>
                {
                    _apiVersion = 4;
                    _hasSnapshot = true;
                    CollectorVersion = "Agent API v4";
                    CollectorSummary = "Viewer 주도형 Telnet 중계 · Agent 연결 확인 중";
                    ReadOnlyQueryMaxCommandLength = 128;
                    ReloadManagedDevices();
                    if (_monitoringStore is not null)
                    {
                        ApplyEvents(_monitoringStore.LoadEvents(), false);
                        ApplyEvents(_monitoringStore.BeginSession(_lastManagedDeviceProfiles), false);
                    }
                    RebuildCollectorHealth(DateTimeOffset.UtcNow);
                }).ConfigureAwait(false);
                try
                {
                    await _client.StartAsync(cancellationToken).ConfigureAwait(false);
                    var identity = await _client.GetIdentityAsync(cancellationToken).ConfigureAwait(false);
                    bool settingsSaved;
                    string settingsSaveErrorCode;
                    lock (_settingsSync)
                    {
                        settingsSaved = _settingsSaveCoordinator.TrySave(
                            _settings,
                            "settings-save-connection",
                            out settingsSaveErrorCode);
                    }
                    await RunOnUiAsync(() =>
                    {
                        _apiVersion = 4;
                        _currentAgentId = identity.AgentId;
                        _hasSnapshot = true;
                        _lastRefreshedAt = DateTimeOffset.UtcNow;
                        _lastSuccessfulReceiptAt = DateTimeOffset.Now;
                        HttpConnectionState = _settings.DemoMode
                            ? AgentConnectionState.Demo
                            : AgentConnectionState.Connected;
                        RealtimeConnectionState = HttpConnectionState;
                        CollectorVersion = $"Agent {identity.AgentId} · API v4";
                        CollectorSummary = "Viewer 주도형 Telnet 중계 준비";
                        ReadOnlyQueryMaxCommandLength = 128;
                        ReadOnlyQueryMaxOutputBytes = identity.MaxOutputBytes;
                        RebuildCollectorHealth(DateTimeOffset.UtcNow);
                        OperationMessage = settingsSaved
                            ? ManagedDeviceStoreWarning(_managedDeviceLoadStatus)
                              ?? "Agent 연결됨 · 장비와 계정은 이 Viewer에서 관리합니다."
                            : SettingsWriteFailureOperationMessage(settingsSaveErrorCode);
                        OnPropertyChanged(nameof(LastRefreshText));
                        OnPropertyChanged(nameof(LastSuccessfulReceiptAt));
                        OnPropertyChanged(nameof(LastSuccessfulReceiptText));
                    }).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    await SetUnavailableStateAsync(exception, AgentChannel.Http).ConfigureAwait(false);
                }
                if (_monitoringStore is not null && _deviceStore is not null)
                {
                    _monitorLoop = Task.Run(() => MonitorLoopAsync(_lifetime.Token));
                }
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
                await RunOnUiAsync(() => SetReadyOperationMessageIfHealthy(_settings)).ConfigureAwait(false);
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
        if (IsReadOnlyQueryRunning) CancelReadOnlyQuery();
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
                if (replacement.SupportsStatelessV4)
                {
                    await replacement.StartAsync(cancellationToken).ConfigureAwait(false);
                    var identity = await replacement.GetIdentityAsync(cancellationToken).ConfigureAwait(false);
                    lock (_settingsSync)
                    {
                        MergeLatestRuntimeSettings(clean);
                        _settingsSaveCoordinator.SaveOrThrow(
                            clean,
                            "settings-save-connection");
                        _settings = clean;
                        Interlocked.Increment(ref _settingsGeneration);
                    }
                    var oldClient = _client;
                    UnsubscribeClient(oldClient);
                    _client = replacement;
                    _statelessV4 = true;
                    _currentAgentId = identity.AgentId;
                    SubscribeClient(replacement);
                    await RunOnUiAsync(() =>
                    {
                        RecentEvents.Clear();
                        _eventsById.Clear();
                        _apiVersion = 4;
                        _hasSnapshot = true;
                        _lastRefreshedAt = DateTimeOffset.UtcNow;
                        _lastSuccessfulReceiptAt = DateTimeOffset.Now;
                        HttpConnectionState = clean.DemoMode ? AgentConnectionState.Demo : AgentConnectionState.Connected;
                        RealtimeConnectionState = HttpConnectionState;
                        CollectorVersion = $"Agent {identity.AgentId} · API v4";
                        CollectorSummary = "Viewer 주도형 Telnet 중계 준비";
                        ReadOnlyQueryMaxOutputBytes = identity.MaxOutputBytes;
                        ReloadManagedDevices();
                        if (_monitoringStore is not null)
                        {
                            ApplyEvents(_monitoringStore.LoadEvents(), false);
                        }
                        RebuildCollectorHealth(DateTimeOffset.UtcNow);
                        OperationMessage = ManagedDeviceStoreWarning(_managedDeviceLoadStatus)
                                           ?? "Agent 연결 설정을 저장했습니다.";
                    }).ConfigureAwait(false);
                    if (_monitoringStore is not null
                        && _deviceStore is not null
                        && (_monitorLoop is null || _monitorLoop.IsCompleted)
                        && !_lifetime.IsCancellationRequested)
                    {
                        _monitorLoop = Task.Run(() => MonitorLoopAsync(_lifetime.Token));
                    }
                    if (!ReferenceEquals(oldClient, replacement))
                    {
                        try { await oldClient.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException) { }
                    }
                    return;
                }

                var snapshot = await replacement.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
                var recent = await replacement.GetRecentEventsAsync(EventPageSize, cancellationToken).ConfigureAwait(false);
                await replacement.StartAsync(cancellationToken).ConfigureAwait(false);

                var candidateHadCursor = clean.TryGetEventCursor(snapshot.AgentId, out var replacementCursor);
                if (!candidateHadCursor)
                {
                    replacementCursor = snapshot.HighWatermark;
                    clean.SetEventCursor(snapshot.AgentId, replacementCursor);
                }
                var targetIdentity = clean.BuildAgentIdentity(snapshot.AgentId);
                lock (_settingsSync)
                {
                    var latest = ViewerSettingsSanitizer.Copy(_settings);
                    if (latest.EventCursors.TryGetValue(targetIdentity, out var latestCursor))
                    {
                        replacementCursor = latestCursor;
                    }
                }
                _ = await replacement.GetEventChangesAsync(replacementCursor, 1, cancellationToken).ConfigureAwait(false);
                await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                IAgentClient previous;
                try
                {
                    lock (_settingsSync)
                    {
                        var hasCursor = MergeLatestRuntimeSettings(
                            clean,
                            snapshot.AgentId,
                            candidateHadCursor,
                            out replacementCursor);
                        _settingsSaveCoordinator.SaveOrThrow(
                            clean,
                            "settings-save-connection");
                        _settings = clean;
                        Interlocked.Increment(ref _settingsGeneration);
                        candidateHadCursor = hasCursor;
                    }
                    previous = _client;
                    UnsubscribeClient(previous);
                    _client = replacement;
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

                var feedResetBefore = Interlocked.Read(ref _feedResetCount);
                var synchronized = false;
                try
                {
                    await SynchronizeChangesAsync(candidateHadCursor, cancellationToken).ConfigureAwait(false);
                    synchronized = true;
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
                if (synchronized && Interlocked.Read(ref _feedResetCount) == feedResetBefore)
                {
                    await RunOnUiAsync(() => SetReadyOperationMessageIfHealthy(clean)).ConfigureAwait(false);
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
        try
        {
            while (true)
            {
                await Task.Delay(SnapshotInterval, cancellationToken).ConfigureAwait(false);
                _ = await RefreshSnapshotAndChangesAsync(true, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await RunMonitoringCycleSafelyAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(SnapshotInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    internal async Task RunMonitoringCycleSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunMonitoringCycleAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsMonitoringPersistenceFailure(exception))
        {
            await ReportMonitoringCycleFailureAsync(
                "VIEWER_MONITOR_STATE_WRITE_FAILED").ConfigureAwait(false);
        }
        catch
        {
            await ReportMonitoringCycleFailureAsync(
                "VIEWER_MONITOR_CYCLE_FAILED").ConfigureAwait(false);
        }
    }

    internal async Task RunMonitoringCycleAsync(CancellationToken cancellationToken = default)
    {
        if (!_statelessV4 || _deviceStore is null || _monitoringStore is null || _disposed) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _monitorGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            try
            {
                await _client.StartAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await SetUnavailableStateAsync(exception, AgentChannel.Http).ConfigureAwait(false);
                _monitoringStore.Heartbeat();
                return;
            }
            var profiles = _deviceStore.Load().Where(item =>
                    item.MonitoringEnabled
                    && item.ConnectionVerified
                    && !IsMonitoringCredentialBlocked(item.Id))
                .ToArray();
            await Task.WhenAll(profiles.Select(profile =>
                MonitorDeviceAsync(profile, linked.Token))).ConfigureAwait(false);
            _monitoringStore.Heartbeat();
        }
        finally
        {
            _monitorGate.Release();
        }
    }

    private static bool LooksUnsupported(string output)
    {
        var lines = (output ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(8);
        return lines.Any(line =>
            line.StartsWith("% Invalid", StringComparison.OrdinalIgnoreCase)
            || line.Contains("invalid command", StringComparison.OrdinalIgnoreCase)
            || line.Contains("invalid input", StringComparison.OrdinalIgnoreCase)
            || line.Contains("unknown command", StringComparison.OrdinalIgnoreCase)
            || line.Contains("unrecognized command", StringComparison.OrdinalIgnoreCase)
            || line.Contains("ambiguous command", StringComparison.OrdinalIgnoreCase)
            || line.Contains("incomplete command", StringComparison.OrdinalIgnoreCase)
            || line.Contains("syntax error", StringComparison.OrdinalIgnoreCase)
            || line.Contains("지원하지", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAuthenticationFailure(string code) => code is
        "AUTH_FAILED" or
        "ENABLE_FAILED" or
        "ENABLE_AUTH_FAILED" or
        "CREDENTIAL_REJECTED";

    private async Task RefreshAsync()
    {
        if (_statelessV4)
        {
            await RunOnUiAsync(() =>
            {
                IsBusy = true;
                ReloadManagedDevices();
                OperationMessage = ManagedDeviceStoreWarning(_managedDeviceLoadStatus)
                                   ?? "Agent 연결 상태를 확인하는 중입니다.";
            }).ConfigureAwait(false);
            try
            {
                await _client.StartAsync(_lifetime.Token).ConfigureAwait(false);
                var identity = await _client.GetIdentityAsync(_lifetime.Token).ConfigureAwait(false);
                bool settingsSaved;
                string settingsSaveErrorCode;
                lock (_settingsSync)
                {
                    settingsSaved = _settingsSaveCoordinator.TrySave(
                        _settings,
                        "settings-save-connection",
                        out settingsSaveErrorCode);
                }
                await RunOnUiAsync(() =>
                {
                    _currentAgentId = identity.AgentId;
                    _lastRefreshedAt = DateTimeOffset.UtcNow;
                    _lastSuccessfulReceiptAt = DateTimeOffset.Now;
                    HttpConnectionState = _settings.DemoMode
                        ? AgentConnectionState.Demo
                        : AgentConnectionState.Connected;
                    RealtimeConnectionState = HttpConnectionState;
                    CollectorVersion = $"Agent {identity.AgentId} · API v4";
                    CollectorSummary = "Viewer 주도형 Telnet 중계 준비";
                    ReadOnlyQueryMaxOutputBytes = identity.MaxOutputBytes;
                    RebuildCollectorHealth(DateTimeOffset.UtcNow);
                    OperationMessage = settingsSaved
                        ? ManagedDeviceStoreWarning(_managedDeviceLoadStatus)
                          ?? "Agent 연결 확인 완료 · Viewer 장비 목록을 새로고침했습니다."
                        : SettingsWriteFailureOperationMessage(settingsSaveErrorCode);
                    OnPropertyChanged(nameof(LastRefreshText));
                    OnPropertyChanged(nameof(LastSuccessfulReceiptAt));
                    OnPropertyChanged(nameof(LastSuccessfulReceiptText));
                }).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await SetUnavailableStateAsync(exception, AgentChannel.Http).ConfigureAwait(false);
            }
            finally
            {
                await RunOnUiAsync(() => IsBusy = false).ConfigureAwait(false);
            }
            return;
        }

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
                await RunOnUiAsync(() =>
                {
                    if (ConnectionState is AgentConnectionState.Connected or AgentConnectionState.Demo)
                    {
                        OperationMessage = "최신 상태로 갱신했습니다.";
                    }
                }).ConfigureAwait(false);
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
            await RunOnUiAsync(ClearConnectionFailureMessageIfHealthy).ConfigureAwait(false);
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
        if (_statelessV4)
        {
            ReadOnlyQueryCommand = SelectedCheckId switch
            {
                "log_ram" => "show sylog tail num 100",
                "interface_status" => "show port status",
                "system" => "show system",
                "version" => "show version",
                _ => "show port status"
            };
            await ExecuteReadOnlyQueryAsync().ConfigureAwait(false);
            return;
        }
        await RunOnUiAsync(() =>
        {
            IsBusy = true;
            OperationMessage = $"{device.Name} · {SelectedCheckDisplayName} 점검 요청 중";
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

    private async Task ExecuteReadOnlyQueryAsync()
    {
        var device = SelectedDevice;
        var command = ReadOnlyQueryCommand.Trim();
        if (device is null || !ReadOnlyQueriesEnabled || command.Length == 0) return;
        if (!ManagedDeviceValidator.IsSingleShowCommand(command, ReadOnlyQueryMaxCommandLength))
        {
            await RunOnUiAsync(() =>
            {
                ReadOnlyQueryStatusText = "실패 · QUERY_COMMAND_BLOCKED";
                ReadOnlyQueryResultMeta = "한 줄짜리 show 조회 명령만 입력할 수 있습니다.";
            }).ConfigureAwait(false);
            return;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var queryContextGeneration = Interlocked.Read(ref _readOnlyQueryContextGeneration);
        _readOnlyQueryCancellation?.Dispose();
        _readOnlyQueryCancellation = cancellation;
        await RunOnUiAsync(() =>
        {
            IsReadOnlyQueryRunning = true;
            ReadOnlyQueryOutput = string.Empty;
            ReadOnlyQueryTruncated = false;
            ReadOnlyQueryStatusText = "장비 연결 및 조회 중";
            ReadOnlyQueryResultMeta = $"{device.Name} · {command}";
        }).ConfigureAwait(false);

        try
        {
            ReadOnlyQueryResultDto result;
            if (_statelessV4 && _deviceStore is not null)
            {
                var profile = _deviceStore.Load().FirstOrDefault(item => item.Id.Equals(device.Id, StringComparison.Ordinal))
                              ?? throw new AgentClientException("VIEWER_DEVICE_NOT_FOUND", AgentConnectionState.Stale);
                var secrets = _deviceStore.GetSecrets(profile.Id);
                var request = new TelnetExecuteRequestDto(
                    Guid.NewGuid().ToString("N"),
                    profile.Host,
                    23,
                    profile.Model,
                    secrets.Username,
                    secrets.Password,
                    secrets.EnablePassword,
                    "manual",
                    [command]);
                var operationGate = GetDeviceOperationGate(profile.Host);
                await operationGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
                TelnetExecutionResultDto execution;
                try
                {
                    execution = await _client.ExecuteTelnetAsync(request, cancellation.Token).ConfigureAwait(false);
                }
                finally
                {
                    operationGate.Release();
                }
                var output = execution.Commands.FirstOrDefault()
                             ?? new TelnetCommandOutputDto(command, string.Empty, false, execution.CompletedUtc);
                result = new ReadOnlyQueryResultDto(
                    4,
                    device.Id,
                    output.Command,
                    execution.StartedUtc,
                    execution.CompletedUtc,
                    execution.DurationMs,
                    output.Output,
                    output.Truncated,
                    execution.SessionCount,
                    execution.ReconnectCount);
            }
            else
            {
                result = await _client.ExecuteReadOnlyQueryAsync(device.Id, command, cancellation.Token).ConfigureAwait(false);
            }
            await RunOnUiAsync(() =>
            {
                if (queryContextGeneration != Interlocked.Read(ref _readOnlyQueryContextGeneration)) return;
                AddReadOnlyQueryHistory(result.Command);
                ReadOnlyQueryOutput = result.Output;
                ReadOnlyQueryTruncated = result.Truncated;
                ReadOnlyQueryStatusText = result.Truncated
                    ? "완료 · 연결 종료됨 · 출력 일부 생략"
                    : "완료 · 연결 종료됨";
                var bytes = Encoding.UTF8.GetByteCount(result.Output);
                var reconnect = result.ReconnectCount > 0 ? $" · 재연결 {result.ReconnectCount}회" : string.Empty;
                ReadOnlyQueryResultMeta =
                    $"{device.Name} · {result.Command} · {result.ElapsedMs:N0}ms · {bytes:N0}바이트 · 세션 {result.SessionCount}회{reconnect}";
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            await RunOnUiAsync(() =>
            {
                if (queryContextGeneration != Interlocked.Read(ref _readOnlyQueryContextGeneration)) return;
                ReadOnlyQueryStatusText = "취소됨 · 연결 종료 요청됨";
                ReadOnlyQueryResultMeta = $"{device.Name} · {command} · 사용자가 취소했습니다.";
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var code = SafeMessage(exception);
            await RunOnUiAsync(() =>
            {
                if (queryContextGeneration != Interlocked.Read(ref _readOnlyQueryContextGeneration)) return;
                ReadOnlyQueryStatusText = $"실패 · {code}";
                ReadOnlyQueryResultMeta = ViewerConnectionMessages.ForCode(code);
            }).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiAsync(() => IsReadOnlyQueryRunning = false).ConfigureAwait(false);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _readOnlyQueryCancellation, null, cancellation), cancellation))
            {
                cancellation.Dispose();
            }
        }
    }

    private void CancelReadOnlyQuery()
    {
        if (!IsReadOnlyQueryRunning) return;
        ReadOnlyQueryStatusText = "취소 요청 중";
        _readOnlyQueryCancellation?.Cancel();
    }

    private void ClearReadOnlyQueryOutput()
    {
        if (IsReadOnlyQueryRunning) return;
        ReadOnlyQueryOutput = string.Empty;
        ReadOnlyQueryTruncated = false;
        ReadOnlyQueryStatusText = "준비";
        ReadOnlyQueryResultMeta = "실행 결과가 없습니다.";
    }

    private void AddReadOnlyQueryHistory(string command)
    {
        if (_readOnlyQueryHistory.Count == 0
            || !_readOnlyQueryHistory[^1].Equals(command, StringComparison.Ordinal))
        {
            _readOnlyQueryHistory.Add(command);
            if (_readOnlyQueryHistory.Count > 20) _readOnlyQueryHistory.RemoveAt(0);
        }
        _readOnlyQueryHistoryIndex = _readOnlyQueryHistory.Count;
        _readOnlyQueryHistoryDraft = string.Empty;
    }

    public bool MoveReadOnlyQueryHistory(int direction)
    {
        if (IsReadOnlyQueryRunning || _readOnlyQueryHistory.Count == 0 || direction == 0) return false;
        if (_readOnlyQueryHistoryIndex >= _readOnlyQueryHistory.Count)
        {
            _readOnlyQueryHistoryDraft = ReadOnlyQueryCommand;
        }

        var next = Math.Clamp(_readOnlyQueryHistoryIndex + Math.Sign(direction), 0, _readOnlyQueryHistory.Count);
        if (next == _readOnlyQueryHistoryIndex) return false;
        _readOnlyQueryHistoryIndex = next;
        _movingReadOnlyQueryHistory = true;
        try
        {
            ReadOnlyQueryCommand = next == _readOnlyQueryHistory.Count
                ? _readOnlyQueryHistoryDraft
                : _readOnlyQueryHistory[next];
        }
        finally
        {
            _movingReadOnlyQueryHistory = false;
        }
        return true;
    }

    private async Task AcknowledgeAsync(EventViewModel? item)
    {
        if (item is null || item.Acknowledged) return;
        try
        {
            if (_statelessV4 && _monitoringStore is not null)
            {
                if (_monitoringStore.Acknowledge(item.AgentEventId))
                {
                    await RunOnUiAsync(() =>
                    {
                        item.Acknowledged = true;
                        RebuildFilteredEvents();
                        NotifySummaryChanged();
                    }).ConfigureAwait(false);
                }
                return;
            }
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
        ReadOnlyQueriesEnabled = snapshot.ApiVersion >= 3 && snapshot.ReadOnlyQueriesEnabled;
        ReadOnlyQueryMaxCommandLength = snapshot.ReadOnlyQueryMaxCommandLength;
        ReadOnlyQueryMaxOutputBytes = snapshot.ReadOnlyQueryMaxOutputBytes;
        OnPropertyChanged(nameof(ReadOnlyQueryUnavailableText));
        _snapshotOperationalStatuses = snapshot.OperationalStatuses ?? [];
        _lastRefreshedAt = snapshot.GeneratedAt;
        _lastSuccessfulReceiptAt = DateTimeOffset.Now;
        _hasSnapshot = true;
        OnPropertyChanged(nameof(ReadOnlyQueryUnavailableText));
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
        SortDevicesByPriority();

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
            if (_statelessV4)
            {
                HttpConnectionState = state;
                RealtimeConnectionState = state;
                if (state is AgentConnectionState.Connected or AgentConnectionState.Demo)
                {
                    _lastSuccessfulReceiptAt = DateTimeOffset.Now;
                    OnPropertyChanged(nameof(LastSuccessfulReceiptAt));
                    OnPropertyChanged(nameof(LastSuccessfulReceiptText));
                    ClearConnectionFailureMessageIfHealthy();
                }
                return;
            }
            RealtimeConnectionState = state;
        });
        if (!_statelessV4 && state == AgentConnectionState.Connected && _initialized) _ = ReconnectCatchupAsync();
    }

    private async Task MonitorDeviceAsync(
        ManagedDeviceProfile profile,
        CancellationToken cancellationToken)
    {
        await _monitorConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        var operationGate = GetDeviceOperationGate(profile.Host);
        var entered = false;
        try
        {
            entered = await operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!entered) return;

            var secrets = _deviceStore!.GetSecrets(profile.Id);
            var knownCapabilities = _monitoringStore!.LoadCapabilities(profile.Id);
            if (!MonitoringProfiles.TryGet(profile.Model, out var commandProfile))
            {
                throw new AgentClientException("VIEWER_DEVICE_INVALID", AgentConnectionState.Stale);
            }
            var definitions = new[]
            {
                commandProfile.GetRequiredCommand(CommandIds.InterfaceStatus),
                commandProfile.GetRequiredCommand(CommandIds.LogRam)
            };
            var selections = new Dictionary<string, string>(StringComparer.Ordinal);
            var commands = new List<string>(2);
            foreach (var definition in definitions)
            {
                var capability = knownCapabilities.FirstOrDefault(item =>
                    item.CommandId.Equals(definition.Id, StringComparison.Ordinal));
                if (capability is { Supported: false, State: "Unsupported" }) continue;

                var selected = SelectMonitoringCommand(definition, capability);
                selections[definition.Id] = selected;
                commands.Add(selected);
            }

            if (commands.Count == 0)
            {
                var testRequest = new TelnetTargetDto(
                    Guid.NewGuid().ToString("N"),
                    profile.Host,
                    23,
                    profile.Model,
                    secrets.Username,
                    secrets.Password,
                    secrets.EnablePassword,
                    "monitor");
                var tested = await _client.TestTelnetAsync(testRequest, cancellationToken).ConfigureAwait(false);
                if (!tested.Success)
                {
                    throw new AgentClientException("AGENT_RESPONSE_INVALID", AgentConnectionState.Stale);
                }
                var testRecoveries = _monitoringStore.RecordSuccess(profile);
                if (testRecoveries.Count > 0) ApplyEvents(testRecoveries);
                await RunOnUiAsync(() => UpdateManagedDevicePresentation(profile.Id)).ConfigureAwait(false);
                return;
            }

            var request = new TelnetExecuteRequestDto(
                Guid.NewGuid().ToString("N"),
                profile.Host,
                23,
                profile.Model,
                secrets.Username,
                secrets.Password,
                secrets.EnablePassword,
                "monitor",
                commands);
            var result = await _client.ExecuteTelnetAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.Success)
            {
                throw new AgentClientException("AGENT_RESPONSE_INVALID", AgentConnectionState.Stale);
            }
            var outputs = result.Commands.ToList();
            var recoveries = _monitoringStore.RecordSuccess(profile);
            if (recoveries.Count > 0) ApplyEvents(recoveries);

            foreach (var definition in definitions)
            {
                if (!selections.TryGetValue(definition.Id, out var selected)) continue;
                await ProbeAndRecordMonitoredOutputAsync(
                    profile,
                    secrets,
                    definition,
                    knownCapabilities.FirstOrDefault(item =>
                        item.CommandId.Equals(definition.Id, StringComparison.Ordinal)),
                    outputs.FirstOrDefault(item =>
                        item.Command.Equals(selected, StringComparison.OrdinalIgnoreCase)),
                    cancellationToken).ConfigureAwait(false);
            }
            await RunOnUiAsync(() => UpdateManagedDevicePresentation(profile.Id)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsMonitoringPersistenceFailure(exception))
        {
            await ReportMonitoringCycleFailureAsync(
                "VIEWER_MONITOR_STATE_WRITE_FAILED").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var code = SafeMessage(exception);
            var authenticationFailure = IsAuthenticationFailure(code);
            if (authenticationFailure)
            {
                // Runtime blocking must happen before any persistence call.
                // Otherwise a full or read-only disk could cause repeated bad
                // password attempts and lock the switch account.
                BlockMonitoringForCredentialFailure(profile.Id);
            }

            var monitoringStateSaveFailed = false;
            if (IsAgentChannelFailure(code))
            {
                await SetUnavailableStateAsync(exception, AgentChannel.Http).ConfigureAwait(false);
            }
            else if (!IsTransientBusy(code))
            {
                try
                {
                    var failures = _monitoringStore!.RecordFailure(profile, code);
                    if (failures.Count > 0) ApplyEvents(failures);
                    await RunOnUiAsync(() => UpdateManagedDevicePresentation(profile.Id)).ConfigureAwait(false);
                }
                catch (Exception persistenceException)
                    when (IsMonitoringPersistenceFailure(persistenceException))
                {
                    monitoringStateSaveFailed = true;
                    await ReportMonitoringCycleFailureAsync(
                        "VIEWER_MONITOR_STATE_WRITE_FAILED").ConfigureAwait(false);
                }
            }
            if (authenticationFailure)
            {
                var deviceSettingsSaveFailed = false;
                try
                {
                    _deviceStore!.MarkConnectionTest(profile.Id, false, code);
                }
                catch
                {
                    deviceSettingsSaveFailed = true;
                }
                await RunOnUiAsync(() =>
                {
                    ReloadManagedDevices(profile.Id);
                    OperationMessage = (monitoringStateSaveFailed, deviceSettingsSaveFailed) switch
                    {
                        (true, true) =>
                            $"{profile.DisplayName} 인증 실패 · 이 실행에서 감시를 즉시 차단했습니다. "
                            + "감시 이력과 장비 설정 파일 저장은 실패했습니다. · VIEWER_MONITOR_STATE_WRITE_FAILED",
                        (true, false) =>
                            $"{profile.DisplayName} 인증 실패 · 계정 잠금 방지를 위해 감시를 껐습니다. "
                            + "감시 이력 저장은 실패했습니다. · VIEWER_MONITOR_STATE_WRITE_FAILED",
                        (false, true) =>
                            $"{profile.DisplayName} 인증 실패 · 이 실행에서 감시를 즉시 차단했습니다. "
                            + "장비 설정 파일 저장은 실패했습니다.",
                        _ =>
                            $"{profile.DisplayName} 인증 실패 · 계정 잠금 방지를 위해 감시를 껐습니다."
                    };
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            if (entered) operationGate.Release();
            _monitorConcurrency.Release();
        }
    }

    private async Task ProbeAndRecordMonitoredOutputAsync(
        ManagedDeviceProfile profile,
        ManagedDeviceSecrets secrets,
        ReadOnlyCommandDefinition definition,
        CollectorCapabilityDto? previousCapability,
        TelnetCommandOutputDto? initialOutput,
        CancellationToken cancellationToken)
    {
        var assessment = AssessMonitoringOutput(definition.Id, initialOutput);
        if (assessment.ExplicitlyUnsupported)
        {
            foreach (var alternate in definition.CandidateCommands.Where(candidate =>
                         !candidate.Equals(initialOutput?.Command, StringComparison.OrdinalIgnoreCase)))
            {
                var fallbackRequest = new TelnetExecuteRequestDto(
                    Guid.NewGuid().ToString("N"),
                    profile.Host,
                    23,
                    profile.Model,
                    secrets.Username,
                    secrets.Password,
                    secrets.EnablePassword,
                    "monitor",
                    [alternate]);
                var fallback = await _client.ExecuteTelnetAsync(fallbackRequest, cancellationToken)
                    .ConfigureAwait(false);
                if (!fallback.Success)
                {
                    throw new AgentClientException("AGENT_RESPONSE_INVALID", AgentConnectionState.Stale);
                }
                assessment = AssessMonitoringOutput(
                    definition.Id,
                    fallback.Commands.FirstOrDefault(item =>
                        item.Command.Equals(alternate, StringComparison.OrdinalIgnoreCase)));
                if (!assessment.ExplicitlyUnsupported) break;
            }
        }

        if (assessment.Ready)
        {
            var selected = assessment.Output!.Command;
            _monitoringStore!.RecordCapability(
                profile.Id,
                new CollectorCapabilityDto(
                    definition.Id,
                    true,
                    "Ready",
                    null,
                    definition.Command,
                    selected,
                    definition.CandidateCommands,
                    selected));
            var events = _monitoringStore.RecordOutput(profile, selected, assessment.Output.Output);
            if (events.Count > 0) ApplyEvents(events);
            return;
        }

        var confirmUnsupported = assessment.ExplicitlyUnsupported
                                 && previousCapability is
                                 {
                                     Supported: true,
                                     State: "Degraded",
                                     ErrorCode: "COMMAND_UNSUPPORTED"
                                 };
        var state = confirmUnsupported ? "Unsupported" : "Degraded";
        _monitoringStore!.RecordCapability(
            profile.Id,
            new CollectorCapabilityDto(
                definition.Id,
                !confirmUnsupported,
                state,
                assessment.ErrorCode,
                definition.Command,
                assessment.Output?.Command,
                definition.CandidateCommands,
                previousCapability?.LastSuccessfulCli));
    }

    private static MonitoringOutputAssessment AssessMonitoringOutput(
        string commandId,
        TelnetCommandOutputDto? output)
    {
        if (output is null)
        {
            return new MonitoringOutputAssessment(null, false, false, "COMMAND_OUTPUT_MISSING");
        }
        if (output.Truncated)
        {
            return new MonitoringOutputAssessment(output, false, false, "OUTPUT_TRUNCATED");
        }
        if (string.IsNullOrWhiteSpace(output.Output))
        {
            return new MonitoringOutputAssessment(output, false, false, "COMMAND_OUTPUT_EMPTY");
        }
        if (LooksUnsupported(output.Output))
        {
            return new MonitoringOutputAssessment(output, false, true, "COMMAND_UNSUPPORTED");
        }

        DiagnosticError? error = commandId switch
        {
            CommandIds.InterfaceStatus => InterfaceStatusOutputParser.Parse(output.Output).Error,
            CommandIds.LogRam => LogOutputParser.Parse(output.Output).Error,
            _ => new DiagnosticError(ErrorCodes.ParserUnsupported, "monitor-output", "Unknown command ID.")
        };
        return error is null
            ? new MonitoringOutputAssessment(output, true, false, null)
            : new MonitoringOutputAssessment(output, false, false, error.Code);
    }

    private static string SelectMonitoringCommand(
        ReadOnlyCommandDefinition definition,
        CollectorCapabilityDto? capability)
    {
        var selected = capability?.State.Equals("Ready", StringComparison.OrdinalIgnoreCase) == true
            ? capability.SelectedCli
            : capability?.LastSuccessfulCli;
        return selected is not null
               && definition.CandidateCommands.Contains(selected, StringComparer.OrdinalIgnoreCase)
            ? selected
            : definition.Command;
    }

    private DeviceSnapshotDto CreateManagedDeviceSnapshot(ManagedDeviceProfile profile)
    {
        var credentialBlocked = IsMonitoringCredentialBlocked(profile.Id);
        var credentialCorrupt = profile.LastConnectionTestCode == "VIEWER_CREDENTIAL_CORRUPT";
        var activeFailure = profile.MonitoringEnabled
            ? _monitoringStore?.GetActiveFailureCode(profile.Id)
            : null;
        var effectiveMonitoringEnabled = profile.MonitoringEnabled && !credentialBlocked;
        var activeInterfaceIssues = effectiveMonitoringEnabled
            ? _monitoringStore?.GetActiveInterfaceConditionCount(profile.Id) ?? 0
            : 0;
        var capabilities = profile.MonitoringEnabled
            ? _monitoringStore?.LoadCapabilities(profile.Id) ?? []
            : [];
        if (profile.MonitoringEnabled && capabilities.Count == 0)
        {
            capabilities =
            [
                new CollectorCapabilityDto(
                    "interface_status",
                    true,
                    "Initializing",
                    PrimaryCli: "show port status",
                    CandidateClis: ["show port status"]),
                new CollectorCapabilityDto(
                    "log_ram",
                    true,
                    "Initializing",
                    PrimaryCli: "show sylog tail num 100",
                    CandidateClis:
                    [
                        "show sylog tail num 100",
                        "show syslog tail num 100",
                        "show log ram"
                    ])
            ];
        }
        var capabilityIssue = capabilities.Any(item =>
            !item.Supported || !item.State.Equals("Ready", StringComparison.OrdinalIgnoreCase));
        var health = activeFailure is not null
            ? DeviceHealth.Disconnected
            : credentialBlocked
                     || credentialCorrupt
                     || !profile.ConnectionVerified
                     || activeInterfaceIssues > 0
                     || (effectiveMonitoringEnabled && capabilityIssue)
                ? DeviceHealth.Warning
                : effectiveMonitoringEnabled ? DeviceHealth.Normal : DeviceHealth.Empty;
        var summary = activeFailure is not null
            ? $"주기 감시 실패 · {activeFailure}"
            : credentialBlocked
            ? "인증 실패 · 접속 시험 후 감시 재개"
            : credentialCorrupt
                ? "저장 계정 사용 불가 · ID/PW 재입력 필요"
            : !profile.ConnectionVerified
                ? $"접속 미확인 · {profile.LastConnectionTestCode ?? "시험 필요"}"
                : activeInterfaceIssues > 0
                    ? $"포트 상태 변경 확인 필요 · {activeInterfaceIssues}개"
                : effectiveMonitoringEnabled && capabilityIssue
                    ? "주기 감시 중 · 일부 명령 확인 필요"
                    : effectiveMonitoringEnabled ? "Viewer 실행 중 주기 감시" : "등록됨 · 주기 감시 꺼짐";
        var capabilityMetric = capabilities.Count == 0
            ? "감시 꺼짐"
            : capabilities.Any(item => item.State.Equals("Initializing", StringComparison.OrdinalIgnoreCase))
                ? "확인 중"
                : $"{capabilities.Count(item => item.Supported)}/{capabilities.Count} 지원";
        var collectionState = credentialBlocked
            ? "AuthBlocked"
            : activeFailure is not null
                ? "Failed"
            : credentialCorrupt
                ? "CredentialCorrupt"
            : activeInterfaceIssues > 0
                ? "Degraded"
            : effectiveMonitoringEnabled && capabilityIssue
                ? "Degraded"
                : effectiveMonitoringEnabled ? "Monitoring" : "Registered";
        var collectionError = credentialBlocked
            ? "AUTH_MONITORING_BLOCKED"
            : activeFailure
              ?? (credentialCorrupt
                  ? "VIEWER_CREDENTIAL_CORRUPT"
                  : activeInterfaceIssues > 0
                      ? "INTERFACE_LINK_DOWN"
                  : capabilities.FirstOrDefault(item =>
                      !item.Supported
                      || !item.State.Equals("Ready", StringComparison.OrdinalIgnoreCase))?.ErrorCode
                    ?? profile.LastConnectionTestCode);
        return new DeviceSnapshotDto(
            profile.Id,
            profile.DisplayName,
            profile.Model,
            profile.Host,
            health,
            profile.LastConnectionTestUtc ?? profile.UpdatedUtc,
            summary,
            "-",
            [
                new("장비 IP", profile.Host),
                new("Telnet", "TCP/23"),
                new("접속 시험", profile.ConnectionVerified ? "성공" : "미확인", profile.ConnectionVerified ? DeviceHealth.Normal : DeviceHealth.Warning),
                new("주기 감시", effectiveMonitoringEnabled ? "켜짐" : "꺼짐", effectiveMonitoringEnabled ? DeviceHealth.Normal : credentialBlocked ? DeviceHealth.Warning : DeviceHealth.Empty),
                new("활성 포트 변경", $"{activeInterfaceIssues}개", activeInterfaceIssues > 0 ? DeviceHealth.Warning : DeviceHealth.Normal),
                new("수집 기능", capabilityMetric, capabilityIssue ? DeviceHealth.Warning : DeviceHealth.Normal)
            ],
            capabilities,
            collectionState,
            collectionError);
    }

    private void UpdateManagedDevicePresentation(string id)
    {
        if (_deviceStore is null) return;
        var profile = _deviceStore.Load().FirstOrDefault(item => item.Id.Equals(id, StringComparison.Ordinal));
        if (profile is null) return;
        var existing = Devices.FirstOrDefault(item => item.Id.Equals(id, StringComparison.Ordinal));
        if (existing is null)
        {
            ReloadManagedDevices(id);
            return;
        }
        existing.Update(CreateManagedDeviceSnapshot(profile));
        SortDevicesByPriority();
        RebuildCollectorHealth(DateTimeOffset.UtcNow);
        NotifySummaryChanged();
    }

    private void SortDevicesByPriority()
    {
        var desired = Devices
            .OrderBy(item => DeviceDisplayPriority(item.Health))
            .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        for (var targetIndex = 0; targetIndex < desired.Length; targetIndex++)
        {
            var currentIndex = Devices.IndexOf(desired[targetIndex]);
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                Devices.Move(currentIndex, targetIndex);
            }
        }
    }

    private static int DeviceDisplayPriority(DeviceHealth health) => health switch
    {
        DeviceHealth.Critical => 0,
        DeviceHealth.Disconnected => 1,
        DeviceHealth.Warning => 2,
        DeviceHealth.Loading => 3,
        DeviceHealth.Normal => 4,
        _ => 5
    };

    private SemaphoreSlim GetDeviceOperationGate(string host)
    {
        var normalizedHost = host.Trim();
        lock (_deviceOperationGateSync)
        {
            if (_deviceOperationGates.TryGetValue(normalizedHost, out var existing)) return existing;
            var created = new SemaphoreSlim(1, 1);
            _deviceOperationGates[normalizedHost] = created;
            return created;
        }
    }

    private static bool IsTransientBusy(string code) =>
        code is "AGENT_BUSY" or "DEVICE_BUSY" or "QUERY_RATE_LIMITED";

    private static bool IsAgentChannelFailure(string code) =>
        (code.StartsWith("AGENT_", StringComparison.Ordinal)
         && !code.Equals("AGENT_BUSY", StringComparison.Ordinal))
        || code.Equals("TLS_IDENTITY_INVALID", StringComparison.Ordinal);

    private static bool IsMonitoringPersistenceFailure(Exception exception) =>
        exception is IOException
        or UnauthorizedAccessException;

    internal bool IsMonitoringCredentialBlocked(string id)
    {
        lock (_monitoringCredentialBlockSync)
        {
            return _monitoringCredentialBlocks.Contains(id);
        }
    }

    private void BlockMonitoringForCredentialFailure(string id)
    {
        lock (_monitoringCredentialBlockSync)
        {
            _monitoringCredentialBlocks.Add(id);
        }
    }

    private void ClearMonitoringCredentialBlock(string id)
    {
        lock (_monitoringCredentialBlockSync)
        {
            _monitoringCredentialBlocks.Remove(id);
        }
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
        var ordered = RecentEvents
            .ToArray()
            .OrderBy(EventDisplayPriority)
            .ThenByDescending(item => item.OccurredAt)
            .ThenByDescending(item => item.Sequence);
        foreach (var item in ordered)
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

    private static int EventDisplayPriority(EventViewModel item)
    {
        if (!item.Recovered && item.Severity is DeviceHealth.Critical or DeviceHealth.Disconnected) return 0;
        if (!item.Recovered && !item.Acknowledged && item.Severity == DeviceHealth.Warning) return 1;
        if (!item.Recovered && !item.Acknowledged) return 2;
        if (item.Recovered) return 3;
        return 4;
    }

    private void RebuildCollectorHealth(DateTimeOffset generatedAt)
    {
        CollectorHealth.Clear();
        CollectorHealth.Add(new("Agent", ConnectionText, ConnectionHealth));
        CollectorHealth.Add(new(_statelessV4 ? "HTTPS API" : "HTTP 상태", HttpConnectionText,
            HttpConnectionState is AgentConnectionState.Connected or AgentConnectionState.Demo ? DeviceHealth.Normal : DeviceHealth.Warning));
        CollectorHealth.Add(new(_statelessV4 ? "Viewer 감시" : "실시간 이벤트", RealtimeConnectionText,
            RealtimeConnectionState is AgentConnectionState.Connected or AgentConnectionState.Demo ? DeviceHealth.Normal : DeviceHealth.Warning));
        CollectorHealth.Add(new("마지막 성공 수신", LastSuccessfulReceiptText));
        CollectorHealth.Add(new("수집기 버전", CollectorVersion));
        CollectorHealth.Add(new("API 버전", $"v{_apiVersion}"));
        CollectorHealth.Add(new("수집기 요약", CollectorSummary));
        CollectorHealth.Add(new("마지막 상태 생성", generatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss")));
        CollectorHealth.Add(new("데이터 범위", _statelessV4
            ? "수동 원문은 화면 메모리만 · 감시 기준값/이벤트는 Viewer 로컬 저장"
            : "구조화 이벤트만 수신 · 원문 미수신"));

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
        OperationalStatuses.Add(_statelessV4
            ? new OperationalStatusDto(
                "VIEWER_LOCAL_MONITOR",
                "Viewer 로컬 감시",
                "Viewer가 실행 중인 동안 등록 장비를 감시합니다.",
                DeviceHealth.Normal)
            : new OperationalStatusDto(
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

        if (!_statelessV4 && OperationalStatuses.All(status =>
                !status.Code.Equals("HTTP_UNPROTECTED", StringComparison.Ordinal)))
        {
            OperationalStatuses.Add(new OperationalStatusDto(
                "HTTP_UNPROTECTED",
                "통신 보호",
                "사내 관리망 전용 · 암호화/인증 없음 · Windows 방화벽 허용 IPv4만 접근",
                DeviceHealth.Warning));
        }
        else if (_statelessV4)
        {
            OperationalStatuses.Add(new OperationalStatusDto(
                "HTTPS_TOFU",
                "HTTPS 보호",
                "Agent 주소 기준으로 서버 인증 정보를 자동 확인합니다.",
                DeviceHealth.Normal));
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

    private string ReadyOperationMessage(ViewerSettings settings)
    {
        if (ManagedDeviceStoreWarning() is { } warning) return warning;
        if (settings.DemoMode) return "오프라인 데모 · 실제 장비에는 접속하지 않습니다.";
        return MonitoredCount == 0
            ? $"Agent 연결됨 · 등록 장비 {TotalCount}대 · 주기 감시 대상 없음"
            : $"Agent 연결됨 · 장비 {MonitoredCount}대 주기 감시 중";
    }

    private string? ManagedDeviceStoreWarning() =>
        ManagedDeviceStoreWarning(_managedDeviceLoadStatus);

    private static string? ManagedDeviceStoreWarning(ManagedDeviceLoadStatus status) => status switch
    {
        ManagedDeviceLoadStatus.Corrupt =>
            "장비 목록 파일 손상 · 안전하게 격리했습니다. 장비를 다시 등록하세요. · VIEWER_DEVICE_STORE_CORRUPT",
        ManagedDeviceLoadStatus.StorageUnavailable =>
            "장비 목록 파일을 읽을 수 없음 · 파일 권한과 잠금을 확인하세요. · VIEWER_DEVICE_STORE_UNAVAILABLE",
        _ => null
    };

    private void SetReadyOperationMessageIfHealthy(ViewerSettings settings)
    {
        if (ConnectionState is AgentConnectionState.Connected or AgentConnectionState.Demo)
        {
            OperationMessage = ReadyOperationMessage(settings);
        }
    }

    private void ClearConnectionFailureMessageIfHealthy()
    {
        if (ConnectionState is not (AgentConnectionState.Connected or AgentConnectionState.Demo)
            || !OperationMessage.StartsWith("Agent 상태 미확인", StringComparison.Ordinal))
        {
            return;
        }

        OperationMessage = ReadyOperationMessage(_settings);
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
        OnPropertyChanged(nameof(UnmonitoredCount));
        OnPropertyChanged(nameof(MonitoredCount));
        OnPropertyChanged(nameof(CriticalDisplayCount));
        OnPropertyChanged(nameof(MiniIssueHealth));
        OnPropertyChanged(nameof(NewLogCount));
        OnPropertyChanged(nameof(RecoveredCount));
        OnPropertyChanged(nameof(UnacknowledgedCount));
        OnPropertyChanged(nameof(UnacknowledgedDisplayText));
        OnPropertyChanged(nameof(AuthoritativeUnacknowledgedCount));
        OnPropertyChanged(nameof(VisibleEventCount));
        OnPropertyChanged(nameof(EventCountText));
        OnPropertyChanged(nameof(ApiVersionText));
        OnPropertyChanged(nameof(NormalSummaryCaption));
        OnPropertyChanged(nameof(MiniCurrentStatusText));
        OnPropertyChanged(nameof(MiniIssueTitle));
        OnPropertyChanged(nameof(MiniIssueDetail));
        foreach (var item in RecentEvents) item.RefreshElapsedText();
    }

    private void NotifyCommandStates()
    {
        (RefreshCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (ManualCheckCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (ExecuteReadOnlyQueryCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        (CancelReadOnlyQueryCommand as RelayCommand)?.NotifyCanExecuteChanged();
        (ClearReadOnlyQueryOutputCommand as RelayCommand)?.NotifyCanExecuteChanged();
    }

    private void RunOnUi(Action action)
    {
        if (_uiContext is null || SynchronizationContext.Current == _uiContext) action();
        else _uiContext.Post(_ => action(), null);
    }

    private void TryReportOperation(string message)
    {
        try
        {
            RunOnUi(() => OperationMessage = message);
        }
        catch
        {
            // UI reporting is best effort. The stable diagnostic code has
            // already been recorded and monitoring must continue.
        }
    }

    private void TryWriteDiagnostic(string stage, string errorCode)
    {
        try
        {
            _writeDiagnostic(stage, errorCode);
        }
        catch
        {
            // Diagnostics must never stop monitoring or shutdown.
        }
    }

    private async Task ReportMonitoringCycleFailureAsync(string errorCode)
    {
        TryWriteDiagnostic("monitoring-cycle", errorCode);
        try
        {
            await RunOnUiAsync(() =>
                {
                    if (OperationMessage.Contains(errorCode, StringComparison.Ordinal))
                    {
                        return;
                    }

                    OperationMessage =
                        $"{ViewerConnectionMessages.ForCode(errorCode)} · {errorCode}";
                })
                .WaitAsync(MonitoringFailureReportTimeout, _lifetime.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // A closed or failing UI context must not terminate monitoring.
        }
    }

    private static string SettingsWriteFailureOperationMessage(string errorCode) =>
        $"{ViewerConnectionMessages.ForCode(errorCode)} · {errorCode}";

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
                await _settingsSaveDelay(TimeSpan.FromSeconds(1), _lifetime.Token).ConfigureAwait(false);
                if (generation != Interlocked.Read(ref _settingsGeneration)) return;
                bool saved;
                string settingsSaveErrorCode;
                lock (_settingsSync)
                {
                    if (generation != Interlocked.Read(ref _settingsGeneration)) return;
                    saved = _settingsSaveCoordinator.TrySave(
                        _settings,
                        "settings-save-background",
                        out settingsSaveErrorCode);
                }
                if (!saved)
                {
                    TryReportOperation(
                        SettingsWriteFailureOperationMessage(settingsSaveErrorCode));
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch
            {
                TryWriteDiagnostic("settings-save-background", "VIEWER_UNEXPECTED_ERROR");
                TryReportOperation(
                    $"{ViewerConnectionMessages.ForCode("VIEWER_UNEXPECTED_ERROR")} · VIEWER_UNEXPECTED_ERROR");
            }
        });
    }

    private void MergeLatestRuntimeSettings(ViewerSettings candidate) =>
        _ = MergeLatestRuntimeSettings(
            candidate,
            targetAgentId: null,
            candidateHadCursor: false,
            out _);

    private bool MergeLatestRuntimeSettings(
        ViewerSettings candidate,
        string? targetAgentId,
        bool candidateHadCursor,
        out long targetCursor)
    {
        var latest = ViewerSettingsSanitizer.Copy(_settings);
        var candidateSnapshot = ViewerSettingsSanitizer.Copy(candidate);
        var targetIdentity = targetAgentId is null
            ? null
            : candidateSnapshot.BuildAgentIdentity(targetAgentId);
        long latestTargetCursor = 0;
        long candidateTargetCursor = 0;
        var latestHadTargetCursor = targetIdentity is not null
                                    && latest.EventCursors.TryGetValue(
                                        targetIdentity,
                                        out latestTargetCursor);
        var candidateHasTargetCursor = targetIdentity is not null
                                       && candidateSnapshot.EventCursors.TryGetValue(
                                           targetIdentity,
                                           out candidateTargetCursor);
        var selectedTargetCursor = latestHadTargetCursor
            ? latestTargetCursor
            : candidateHasTargetCursor
                ? candidateTargetCursor
                : 0;
        targetCursor = selectedTargetCursor;
        candidate.Synchronize(target =>
        {
            target.MiniTopmost = latest.MiniTopmost;
            target.MiniLeft = latest.MiniLeft;
            target.MiniTop = latest.MiniTop;
            target.MainLeft = latest.MainLeft;
            target.MainTop = latest.MainTop;
            target.MainWidth = latest.MainWidth;
            target.MainHeight = latest.MainHeight;
            target.LastEventSequence = targetAgentId is null
                ? latest.LastEventSequence
                : Math.Max(0, selectedTargetCursor);

            var mergedCursors = new Dictionary<string, long>(
                latest.EventCursors,
                StringComparer.Ordinal);
            if (targetIdentity is not null
                && !latestHadTargetCursor
                && candidateHasTargetCursor)
            {
                if (mergedCursors.Count >= 32)
                {
                    mergedCursors.Remove(mergedCursors.Keys.First());
                }
                mergedCursors[targetIdentity] = candidateTargetCursor;
            }
            target.EventCursors = mergedCursors;

            var mergedPins = new Dictionary<string, string>(
                latest.AgentTrustPins,
                StringComparer.OrdinalIgnoreCase);
            if (!candidateSnapshot.DemoMode)
            {
                var targetAuthority = candidateSnapshot.BuildAgentAuthority();
                if (targetAuthority.Length > 0
                    && candidateSnapshot.AgentTrustPins.TryGetValue(targetAuthority, out var targetPin))
                {
                    if (!mergedPins.ContainsKey(targetAuthority) && mergedPins.Count >= 32)
                    {
                        mergedPins.Remove(mergedPins.Keys.First());
                    }
                    mergedPins[targetAuthority] = targetPin;
                }
            }
            target.AgentTrustPins = mergedPins;
        });
        return candidateHadCursor || latestHadTargetCursor;
    }

    private static string SafeMessage(Exception exception) => exception switch
    {
        AgentClientException typed => typed.ErrorCode,
        HttpRequestException => "AGENT_UNREACHABLE",
        TaskCanceledException => "AGENT_TIMEOUT",
        InvalidOperationException invalid when invalid.Message.Contains("CONNECTION", StringComparison.OrdinalIgnoreCase) => "VIEWER_CONNECTION_REQUIRED",
        InvalidOperationException => "VIEWER_CONFIGURATION_INVALID",
        InvalidDataException invalid when invalid.Message == "VIEWER_CREDENTIAL_CORRUPT" => "VIEWER_CREDENTIAL_CORRUPT",
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
        _readOnlyQueryCancellation?.Cancel();
        Interlocked.Increment(ref _settingsGeneration);
        UnsubscribeClient(_client);

        var initializeQuiesced = false;
        var syncQuiesced = false;
        var snapshotQuiesced = _snapshotLoop is null;
        var monitorLoopQuiesced = _monitorLoop is null;
        var monitorGateQuiesced = false;
        try { initializeQuiesced = await _initializeGate.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
        try { syncQuiesced = await _syncGate.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
        catch (ObjectDisposedException) { }

        if (_snapshotLoop is not null)
        {
            try { await _snapshotLoop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
            snapshotQuiesced = _snapshotLoop.IsCompleted;
        }
        if (_monitorLoop is not null)
        {
            try { await _monitorLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
            monitorLoopQuiesced = _monitorLoop.IsCompleted;
        }
        if (monitorLoopQuiesced)
        {
            try { monitorGateQuiesced = await _monitorGate.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }
        try { _monitoringStore?.EndSession(); } catch { }

        try { await _client.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
        Interlocked.Exchange(ref _readOnlyQueryCancellation, null)?.Dispose();
        lock (_settingsSync)
        {
            _settingsSaveCoordinator.TrySave(
                _settings,
                "settings-save-shutdown",
                out _);
        }

        if (initializeQuiesced) _initializeGate.Dispose();
        if (syncQuiesced) _syncGate.Dispose();
        if (monitorGateQuiesced)
        {
            _monitorGate.Dispose();
            _monitorConcurrency.Dispose();
        }
        if (initializeQuiesced
            && syncQuiesced
            && snapshotQuiesced
            && monitorLoopQuiesced
            && monitorGateQuiesced)
        {
            _lifetime.Dispose();
        }
    }
}
