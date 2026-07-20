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
    Connected,
    Disconnected,
    Demo
}

public sealed record DeviceMetricDto(string Label, string Value, DeviceHealth Health = DeviceHealth.Normal);

public sealed record DeviceSnapshotDto(
    string Id,
    string Name,
    string Model,
    string AddressLabel,
    DeviceHealth Health,
    DateTimeOffset LastCheckedAt,
    string Summary,
    string Uptime,
    IReadOnlyList<DeviceMetricDto>? Metrics = null);

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
    string? ConditionKey = null);

public sealed record AgentSnapshotDto(
    DateTimeOffset GeneratedAt,
    AgentConnectionState ConnectionState,
    IReadOnlyList<DeviceSnapshotDto> Devices,
    long LastEventSequence,
    string CollectorVersion,
    string CollectorSummary);

public sealed record CommandResultDto(bool Accepted, string Message, string? ErrorCode = null);

public sealed class DeviceViewModel : Infrastructure.ObservableObject
{
    private DeviceHealth _health;
    private DateTimeOffset _lastCheckedAt;
    private string _summary;
    private string _uptime;

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
        Metrics = new ObservableCollection<DeviceMetricDto>(source.Metrics ?? []);
    }

    public string Id { get; }
    public string Name { get; }
    public string Model { get; }
    public string AddressLabel { get; }
    public ObservableCollection<DeviceMetricDto> Metrics { get; }

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
        Metrics.Clear();
        foreach (var metric in source.Metrics ?? [])
        {
            Metrics.Add(metric);
        }
    }
}

public sealed class EventViewModel : Infrastructure.ObservableObject
{
    private bool _acknowledged;
    private bool _recovered;
    private DeviceHealth _severity;
    private string _title;
    private string _detail;

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
        ConditionKey = source.ConditionKey;
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
        private set => SetProperty(ref _detail, value);
    }
    public bool Recovered
    {
        get => _recovered;
        private set => SetProperty(ref _recovered, value);
    }
    public string? ConditionKey { get; }
    public string TimeText => OccurredAt.LocalDateTime.ToString("MM-dd HH:mm:ss");

    public bool Acknowledged
    {
        get => _acknowledged;
        set => SetProperty(ref _acknowledged, value);
    }

    public void Update(SwitchEventDto source)
    {
        if (!string.Equals(AgentEventId, source.AgentEventId, StringComparison.Ordinal)) return;
        Severity = source.Severity;
        Title = source.Title;
        Detail = source.Detail;
        Acknowledged = source.Acknowledged;
        Recovered = source.Recovered;
    }
}
