using SamsungSwitchWatch.Core.Diagnostics;

namespace SamsungSwitchWatch.Core.Models;

public enum LinkState
{
    Unknown,
    Up,
    Down
}

public enum AdministrativeState
{
    Unknown,
    Enabled,
    Disabled
}

public sealed record VersionSnapshot(
    string? Model,
    string? SoftwareVersion,
    string? HardwareVersion,
    string? MainPowerStatus,
    string? RedundantPowerStatus);

public sealed record SystemSnapshot(
    TimeSpan? Uptime,
    IReadOnlyDictionary<string, string> PostChecks);

public sealed record SwitchLogEntry(
    string Identity,
    int? SequenceNumber,
    DateTime? DeviceTimestamp,
    string Message,
    int? Level,
    int? Module,
    int? Function,
    int? EventNumber);

public sealed record LogSnapshot(IReadOnlyList<SwitchLogEntry> Entries);

public sealed record InterfaceStatus(
    string PortId,
    AdministrativeState AdministrativeState,
    LinkState OperationalState,
    string? Speed,
    string? Duplex);

public sealed record InterfaceStatusSnapshot(IReadOnlyDictionary<string, InterfaceStatus> Interfaces);

public sealed record CollectorIssue(string CommandId, DiagnosticError Error);

public sealed record DeviceSnapshot(
    string DeviceId,
    DateTimeOffset CollectedAt,
    VersionSnapshot? Version,
    SystemSnapshot? System,
    LogSnapshot? Logs,
    InterfaceStatusSnapshot? Interfaces,
    IReadOnlyList<CollectorIssue> Issues);
