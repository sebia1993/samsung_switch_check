using System.Collections.ObjectModel;

namespace SamsungSwitchWatch.Viewer.Models;

public enum DeviceHealth
{
    Normal,
    Warning,
    Critical,
    Disconnected,
    Loading,
    Empty
}

public enum AgentConnectionState
{
    Connecting,
    Reconnecting,
    Connected,
    Offline,
    Stale,
    NeedsPairing,
    Demo
}

public enum EventFilter
{
    All,
    Unacknowledged,
    NewLog,
    Critical,
    Recovered
}

public sealed record DeviceMetricDto(string Label, string Value, DeviceHealth Health = DeviceHealth.Normal);

public sealed record CollectorCapabilityDto(
    string CommandId,
    bool Supported,
    string State,
    string? ErrorCode = null);

public sealed record OperationalStatusDto(
    string Code,
    string Title,
    string Detail,
    DeviceHealth Health);

public sealed record DeviceSnapshotDto(
    string Id,
    string Name,
    string Model,
    string AddressLabel,
    DeviceHealth Health,
    DateTimeOffset LastCheckedAt,
    string Summary,
    string Uptime,
    IReadOnlyList<DeviceMetricDto>? Metrics = null,
    IReadOnlyList<CollectorCapabilityDto>? Capabilities = null,
    string CollectionState = "Initializing",
    string? CollectionErrorCode = null);

public sealed record SwitchEventDto(
    long Sequence,
    string AgentEventId,
    string DeviceId,
    string DeviceName,
    DateTimeOffset OccurredAt,
    DeviceHealth Severity,
    string Kind,
    string Title,
    string Detail,
    bool Acknowledged = false,
    bool Recovered = false,
    string? ConditionKey = null,
    bool IsActiveCondition = false,
    DateTimeOffset? RecoveredAt = null,
    string? NavigationEventId = null);

public sealed record AgentEventChangeDto(
    long ChangeSequence,
    string ChangeKind,
    SwitchEventDto Event);

public sealed record EventChangePageDto(
    long HighWatermark,
    long NextCursor,
    bool HasMore,
    IReadOnlyList<AgentEventChangeDto> Changes,
    bool ResetRequired = false,
    long ResetCursor = 0);

public sealed record AgentSnapshotDto(
    DateTimeOffset GeneratedAt,
    AgentConnectionState ConnectionState,
    IReadOnlyList<DeviceSnapshotDto> Devices,
    long LastEventSequence,
    string CollectorVersion,
    string CollectorSummary,
    string AgentId = "agent",
    bool Ready = true,
    string ReadinessCode = "READY",
    long? AuthoritativeUnacknowledged = null,
    int ApiVersion = 2,
    string AgentChannelStatus = "unknown",
    string ApiChannelStatus = "unknown",
    string RealtimeChannelStatus = "unknown",
    string CertificateStatus = "configured",
    DateTimeOffset? CertificateExpiresAt = null,
    IReadOnlyList<OperationalStatusDto>? OperationalStatuses = null,
    int MaxConcurrentDevices = 1)
{
    public long HighWatermark => LastEventSequence;
}

public sealed record CommandResultDto(bool Accepted, string Message, string? ErrorCode = null);

public sealed class DeviceViewModel : Infrastructure.ObservableObject
{
    private DeviceHealth _health;
    private DateTimeOffset _lastCheckedAt;
    private string _summary;
    private string _uptime;
    private string _collectionState;
    private string? _collectionErrorCode;

    public DeviceViewModel(DeviceSnapshotDto source)
    {
        Id = source.Id;
        Name = source.Name;
        Model = source.Model;
        AddressLabel = source.AddressLabel;
        _health = source.Health;
        _lastCheckedAt = source.LastCheckedAt;
        _summary = source.Summary;
        _uptime = source.Uptime;
        _collectionState = source.CollectionState;
        _collectionErrorCode = source.CollectionErrorCode;
        Metrics = new ObservableCollection<DeviceMetricDto>(source.Metrics ?? []);
        Capabilities = new ObservableCollection<CollectorCapabilityDto>(source.Capabilities ?? []);
    }

    public string Id { get; }
    public string Name { get; }
    public string Model { get; }
    public string AddressLabel { get; }
    public ObservableCollection<DeviceMetricDto> Metrics { get; }
    public ObservableCollection<CollectorCapabilityDto> Capabilities { get; }
    public int SupportedCapabilityCount => Capabilities.Count(item => item.Supported);
    public int CapabilityCount => Capabilities.Count;
    public string CapabilityText => CapabilityCount == 0
        ? "기능 확인 중"
        : $"수집 기능 {SupportedCapabilityCount}/{CapabilityCount}";

    public string CollectionState
    {
        get => _collectionState;
        private set
        {
            if (SetProperty(ref _collectionState, value)) OnPropertyChanged(nameof(CollectionStatusText));
        }
    }

    public string? CollectionErrorCode
    {
        get => _collectionErrorCode;
        private set
        {
            if (SetProperty(ref _collectionErrorCode, value)) OnPropertyChanged(nameof(CollectionStatusText));
        }
    }

    public string CollectionStatusText => string.IsNullOrWhiteSpace(CollectionErrorCode)
        ? CollectionState
        : $"{CollectionState} · {CollectionErrorCode}";

    public DeviceHealth Health
    {
        get => _health;
        set => SetProperty(ref _health, value);
    }

    public DateTimeOffset LastCheckedAt
    {
        get => _lastCheckedAt;
        set
        {
            if (SetProperty(ref _lastCheckedAt, value))
            {
                OnPropertyChanged(nameof(LastCheckedText));
            }
        }
    }

    public string LastCheckedText => LastCheckedAt.LocalDateTime.ToString("HH:mm:ss");

    public string Summary
    {
        get => _summary;
        set => SetProperty(ref _summary, value);
    }

    public string Uptime
    {
        get => _uptime;
        set => SetProperty(ref _uptime, value);
    }

    public void Update(DeviceSnapshotDto source)
    {
        Health = source.Health;
        LastCheckedAt = source.LastCheckedAt;
        Summary = source.Summary;
        Uptime = source.Uptime;
        CollectionState = source.CollectionState;
        CollectionErrorCode = source.CollectionErrorCode;
        Metrics.Clear();
        foreach (var metric in source.Metrics ?? [])
        {
            Metrics.Add(metric);
        }
        Capabilities.Clear();
        foreach (var capability in source.Capabilities ?? []) Capabilities.Add(capability);
        OnPropertyChanged(nameof(SupportedCapabilityCount));
        OnPropertyChanged(nameof(CapabilityCount));
        OnPropertyChanged(nameof(CapabilityText));
    }
}

public sealed class EventViewModel : Infrastructure.ObservableObject
{
    private bool _acknowledged;
    private bool _recovered;
    private DeviceHealth _severity;
    private string _title;
    private string _detail;
    private DateTimeOffset? _recoveredAt;

    public EventViewModel(SwitchEventDto source)
    {
        Sequence = source.Sequence;
        AgentEventId = source.AgentEventId;
        DeviceId = source.DeviceId;
        DeviceName = source.DeviceName;
        OccurredAt = source.OccurredAt;
        _severity = source.Severity;
        Kind = source.Kind;
        _title = source.Title;
        _detail = source.Detail;
        _acknowledged = source.Acknowledged;
        _recovered = source.Recovered;
        _recoveredAt = source.RecoveredAt;
        ConditionKey = source.ConditionKey;
        NavigationEventId = string.IsNullOrWhiteSpace(source.NavigationEventId)
            ? source.AgentEventId
            : source.NavigationEventId;
    }

    public long Sequence { get; }
    public string AgentEventId { get; }
    public string DeviceId { get; }
    public string DeviceName { get; }
    public DateTimeOffset OccurredAt { get; }
    public DeviceHealth Severity
    {
        get => _severity;
        private set => SetProperty(ref _severity, value);
    }
    public string Kind { get; }
    public string Title
    {
        get => _title;
        private set => SetProperty(ref _title, value);
    }
    public string Detail
    {
        get => _detail;
        private set
        {
            if (SetProperty(ref _detail, value)) OnPropertyChanged(nameof(AlertDetail));
        }
    }
    public bool Recovered
    {
        get => _recovered;
        private set
        {
            if (SetProperty(ref _recovered, value)) OnPropertyChanged(nameof(AlertDetail));
        }
    }
    public string? ConditionKey { get; }
    public string NavigationEventId { get; }
    public string TimeText => OccurredAt.LocalDateTime.ToString("MM-dd HH:mm:ss");
    public bool IsLogEvent => Kind.Contains("로그", StringComparison.Ordinal)
                              || Kind.Contains("log", StringComparison.OrdinalIgnoreCase);
    public string StatusText => Recovered ? "복구됨" : Acknowledged ? "확인됨" : "미확인";
    public DateTimeOffset? RecoveredAt
    {
        get => _recoveredAt;
        private set
        {
            if (SetProperty(ref _recoveredAt, value))
            {
                OnPropertyChanged(nameof(RecoveryDuration));
                OnPropertyChanged(nameof(AlertDetail));
            }
        }
    }

    public TimeSpan? RecoveryDuration => RecoveredAt is { } recoveredAt && recoveredAt >= OccurredAt
        ? recoveredAt - OccurredAt
        : null;

    public string AlertDetail => Recovered && RecoveryDuration is { } duration
        ? $"{Detail} · 장애 지속 {FormatDuration(duration)}"
        : Detail;

    public bool Acknowledged
    {
        get => _acknowledged;
        set
        {
            if (SetProperty(ref _acknowledged, value)) OnPropertyChanged(nameof(StatusText));
        }
    }

    public void Update(SwitchEventDto source)
    {
        if (!string.Equals(AgentEventId, source.AgentEventId, StringComparison.Ordinal)) return;
        Severity = source.Severity;
        Title = source.Title;
        Detail = source.Detail;
        Acknowledged = source.Acknowledged;
        Recovered = source.Recovered;
        RecoveredAt = source.RecoveredAt;
        OnPropertyChanged(nameof(AlertDetail));
        OnPropertyChanged(nameof(StatusText));
    }

    private static string FormatDuration(TimeSpan value) => value.TotalHours >= 1
        ? $"{(int)value.TotalHours}시간 {value.Minutes}분"
        : value.TotalMinutes >= 1
            ? $"{(int)value.TotalMinutes}분 {value.Seconds}초"
            : $"{Math.Max(0, (int)value.TotalSeconds)}초";
}
