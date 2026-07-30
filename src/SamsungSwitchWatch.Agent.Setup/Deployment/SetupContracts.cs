using System.Diagnostics;
using System.Globalization;
using System.Net;

namespace SamsungSwitchWatch.Agent.Setup.Deployment;

public static class SetupConstants
{
    public const string ProductName = "SamsungSwitchWatch";
    public const string ServiceName = "SamsungSwitchWatchAgent";
    public const string ServiceDisplayName = "Samsung Switch Watch Agent";
    public const string FirewallRuleName = "SamsungSwitchWatchAgent-Https";
    public const string LegacyFirewallRuleName = "SamsungSwitchWatchAgent-Http";
    public const int HttpsPort = 18443;
    public const string AgentExecutableName = "SamsungSwitchWatch.Agent.exe";
    public const string SetupExecutableName = "SamsungSwitchWatch.Agent.Setup.exe";
    public const string ManifestFileName = "BUILD-MANIFEST.json";
}

public static class SetupErrorCodes
{
    public const string Ok = "OK";
    public const string PackageNotFound = "SETUP_PACKAGE_NOT_FOUND";
    public const string ManifestInvalid = "SETUP_MANIFEST_INVALID";
    public const string PackageHashMismatch = "SETUP_PACKAGE_HASH_MISMATCH";
    public const string ViewerIpInvalid = "SETUP_VIEWER_IP_INVALID";
    public const string NetworkSelectionInvalid = "SETUP_NETWORK_SELECTION_INVALID";
    public const string ExistingNetworksNotLoaded = "SETUP_EXISTING_NETWORKS_NOT_LOADED";
    public const string AdministratorRequired = "SETUP_ADMINISTRATOR_REQUIRED";
    public const string PathInvalid = "SETUP_PATH_INVALID";
    public const string PathUntrusted = "SETUP_PATH_UNTRUSTED";
    public const string PathNotWritable = "SETUP_PATH_NOT_WRITABLE";
    public const string ConfigurationInvalid = "SETUP_CONFIGURATION_INVALID";
    public const string ServiceFailed = "SETUP_SERVICE_FAILED";
    public const string FirewallFailed = "SETUP_FIREWALL_FAILED";
    public const string HealthFailed = "SETUP_HEALTH_FAILED";
    public const string RollbackFailed = "SETUP_ROLLBACK_FAILED";
    public const string RecoveryRequired = "SETUP_RECOVERY_REQUIRED";
    public const string RollbackStateMismatch = "ROLLBACK_STATE_MISMATCH";
    public const string RollbackServiceStopFailed = "ROLLBACK_SERVICE_STOP_FAILED";
    public const string RollbackFileRestoreFailed = "ROLLBACK_FILE_RESTORE_FAILED";
    public const string RollbackDataCleanupFailed = "ROLLBACK_DATA_CLEANUP_FAILED";
    public const string RollbackServiceRestoreFailed = "ROLLBACK_SERVICE_RESTORE_FAILED";
    public const string RollbackHttpsFirewallRestoreFailed =
        "ROLLBACK_HTTPS_FIREWALL_RESTORE_FAILED";
    public const string RollbackLegacyFirewallRestoreFailed =
        "ROLLBACK_LEGACY_FIREWALL_RESTORE_FAILED";
    public const string RollbackJournalWriteFailed = "ROLLBACK_JOURNAL_WRITE_FAILED";
    public const string RollbackEvidenceCleanupFailed =
        "ROLLBACK_EVIDENCE_CLEANUP_FAILED";
    public const string RollbackStagingCleanupFailed =
        "ROLLBACK_STAGING_CLEANUP_FAILED";
    public const string RollbackBackupCleanupFailed =
        "ROLLBACK_BACKUP_CLEANUP_FAILED";
    public const string RollbackFailedDirectoryCleanupFailed =
        "ROLLBACK_FAILED_DIRECTORY_CLEANUP_FAILED";
    public const string RollbackJournalCleanupFailed =
        "ROLLBACK_JOURNAL_CLEANUP_FAILED";
    public const string AlreadyRunning = "SETUP_ALREADY_RUNNING";
    public const string Cancelled = "SETUP_CANCELLED";
    public const string Unexpected = "SETUP_UNEXPECTED";
    public const string DiagnosticWriteFailed = "DIAGNOSTIC_WRITE_FAILED";
}

public sealed class SetupException(string code, string safeMessage, Exception? innerException = null)
    : Exception(safeMessage, innerException)
{
    public string Code { get; } = code;
}

public sealed record DeploymentPaths(
    string PackageDirectory,
    string InstallDirectory,
    string DataDirectory,
    string OperationsDirectory)
{
    public static DeploymentPaths ForCurrentMachine(string packageDirectory)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return new DeploymentPaths(
            Path.GetFullPath(packageDirectory),
            Path.Combine(programFiles, "SamsungSwitchWatch", "Agent"),
            Path.Combine(programData, "SamsungSwitchWatch"),
            Path.Combine(programData, "SamsungSwitchWatch-Operations"));
    }

    public string AgentExecutablePath =>
        Path.Combine(InstallDirectory, SetupConstants.AgentExecutableName);

    public string ProductionConfigurationPath =>
        Path.Combine(InstallDirectory, "appsettings.Production.json");
}

public sealed record AgentPackage(
    string Version,
    string SourceCommit,
    string ExecutablePath,
    string ManifestPath,
    string ExecutableSha256,
    IReadOnlyList<PackageFile> VerifiedFiles);

public sealed record PackageFile(string Name, string Path, string Sha256, long Size);

public sealed record NetworkCandidate(
    string Id,
    string InterfaceName,
    string Address,
    string Cidr,
    string Description);

public sealed record SetupRequest(
    string ViewerIpv4,
    IReadOnlyList<string> TargetCidrs);

public enum SetupStepState
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Warning,
    Information
}

public sealed record SetupStepResult(
    string Code,
    string Label,
    SetupStepState State,
    string Message);

internal sealed record SetupStageDiagnostic(
    string Code,
    SetupStepState State,
    long DurationMilliseconds,
    long ElapsedMilliseconds);

internal sealed record SetupOperationDiagnosticMetadata(
    long DurationMilliseconds,
    IReadOnlyList<SetupStageDiagnostic> Stages,
    IReadOnlyList<string> SafeDecisionCodes);

internal sealed class SetupStepRecorder : IReadOnlyList<SetupStepResult>
{
    private readonly List<SetupStepResult> _steps = [];
    private readonly List<SetupStageDiagnostic> _diagnostics = [];
    private readonly List<string> _safeDecisionCodes = [];
    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();
    private long _previousTimestamp;

    public SetupStepRecorder()
    {
        _previousTimestamp = _startedTimestamp;
    }

    public int Count => _steps.Count;
    public SetupStepResult this[int index] => _steps[index];

    public void Add(SetupStepResult step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var timestamp = Stopwatch.GetTimestamp();
        _steps.Add(step);
        _diagnostics.Add(new SetupStageDiagnostic(
            step.Code,
            step.State,
            ElapsedMilliseconds(_previousTimestamp, timestamp),
            ElapsedMilliseconds(_startedTimestamp, timestamp)));
        _previousTimestamp = timestamp;
    }

    public void AddRange(IEnumerable<SetupStepResult> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        foreach (var step in steps)
        {
            Add(step);
        }
    }

    public void AddSafeDecisionCode(string? code)
    {
        if (!string.IsNullOrWhiteSpace(code) &&
            !_safeDecisionCodes.Contains(code, StringComparer.Ordinal))
        {
            _safeDecisionCodes.Add(code);
        }
    }

    public SetupOperationDiagnosticMetadata Snapshot()
    {
        var timestamp = Stopwatch.GetTimestamp();
        return new SetupOperationDiagnosticMetadata(
            ElapsedMilliseconds(_startedTimestamp, timestamp),
            _diagnostics.ToArray(),
            _safeDecisionCodes.ToArray());
    }

    public IEnumerator<SetupStepResult> GetEnumerator() => _steps.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        GetEnumerator();

    private static long ElapsedMilliseconds(long start, long end) =>
        Math.Max(
            0,
            (long)Math.Round(
                Stopwatch.GetElapsedTime(start, end).TotalMilliseconds,
                MidpointRounding.AwayFromZero));
}

public sealed record SetupOperationResult(
    bool Succeeded,
    string Code,
    string Message,
    IReadOnlyList<SetupStepResult> Steps)
{
    public string? PrimaryFailureCode { get; init; }
    public string? PrimaryFailureMessage { get; init; }
    public IReadOnlyList<string> RollbackFailureCodes { get; init; } = [];
    public string? AgentHealthCode { get; init; }
    public bool AgentRestartObserved { get; init; }
    internal SetupOperationDiagnosticMetadata? DiagnosticMetadata { get; init; }

    public static SetupOperationResult Failure(
        string code,
        string message,
        IReadOnlyList<SetupStepResult> steps) =>
        new(false, code, message, steps)
        {
            DiagnosticMetadata =
                (steps as SetupStepRecorder)?.Snapshot()
        };

    public static SetupOperationResult Success(
        string message,
        IReadOnlyList<SetupStepResult> steps) =>
        new(true, SetupErrorCodes.Ok, message, steps)
        {
            DiagnosticMetadata =
                (steps as SetupStepRecorder)?.Snapshot()
        };
}

public sealed record PendingRecoveryInspection(
    bool Exists,
    bool CanRecover,
    string Code,
    string Message)
{
    public int? JournalFormatVersion { get; init; }
    public string? JournalStage { get; init; }
    public string? PrimaryFailureCode { get; init; }
    public string? PrimaryFailureMessage { get; init; }
    public IReadOnlyList<string> RollbackFailureCodes { get; init; } = [];
    public string? AgentHealthCode { get; init; }
    public bool AgentRestartObserved { get; init; }
    public IReadOnlyList<string> FailureCodes => RollbackFailureCodes;
    public string ServiceState { get; init; } = "unknown";
    public bool EvidenceStateKnown { get; init; } = true;
    public bool InstallDirectoryExists { get; init; }
    public bool StagingDirectoryExists { get; init; }
    public bool BackupDirectoryExists { get; init; }
    public bool FailedDirectoryExists { get; init; }
    public bool DataDirectoryExists { get; init; }

    public static PendingRecoveryInspection None { get; } =
        new(
            false,
            false,
            SetupErrorCodes.Ok,
            "복구가 필요한 이전 설치 작업이 없습니다.");
}

public sealed record ServiceSnapshot(
    bool Exists,
    bool Running,
    string BinaryPath,
    uint StartType,
    string AccountName,
    string DisplayName,
    string Description,
    uint ServiceSidType,
    ServiceRecoverySnapshot Recovery,
    byte[]? SecurityDescriptor,
    int ProcessId)
{
    public static ServiceSnapshot Missing { get; } =
        new(
            false,
            false,
            string.Empty,
            0,
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            ServiceRecoverySnapshot.Empty,
            null,
            0);
}

public sealed record ServiceRecoverySnapshot(
    uint ResetPeriod,
    bool ApplyOnNonCrashFailures,
    string RebootMessage,
    string Command,
    IReadOnlyList<ServiceFailureActionSnapshot> Actions)
{
    public static ServiceRecoverySnapshot Empty { get; } =
        new(0, false, string.Empty, string.Empty, []);
}

public sealed record ServiceFailureActionSnapshot(int Type, uint Delay);

public static class ServiceAccountContract
{
    public static bool IsLegacyLocalService(ServiceSnapshot service) =>
        service.Exists &&
        (string.Equals(
             service.AccountName,
             @"NT AUTHORITY\LocalService",
             StringComparison.OrdinalIgnoreCase) ||
         string.Equals(
             service.AccountName,
             @"NT AUTHORITY\LOCAL SERVICE",
             StringComparison.OrdinalIgnoreCase) ||
         string.Equals(
             service.AccountName,
             "LocalService",
             StringComparison.OrdinalIgnoreCase));

    public static bool AllowsLegacyLocalServiceDataOwner(ServiceSnapshot service) =>
        IsLegacyLocalService(service) && !service.Running;
}

public sealed record FirewallRuleSnapshot(
    bool Exists,
    string Name,
    string Description,
    bool Enabled,
    int Direction,
    int Action,
    int Protocol,
    string LocalPorts,
    string RemoteAddresses,
    int Profiles,
    string InterfaceTypes,
    bool EdgeTraversal,
    string Grouping)
{
    public string ApplicationName { get; init; } = string.Empty;
    public string ServiceName { get; init; } = string.Empty;

    public static FirewallRuleSnapshot Missing(string name) =>
        new(false, name, string.Empty, false, 0, 0, 0, string.Empty,
            string.Empty, 0, string.Empty, false, string.Empty);
}

public sealed record FirewallSecurityWarning(
    string Code,
    string Message);

public sealed record FirewallSecurityAssessment(
    IReadOnlyList<FirewallSecurityWarning> Warnings)
{
    public static FirewallSecurityAssessment Safe { get; } = new([]);
}

public interface IAgentPackageValidator
{
    AgentPackage Validate(string packageDirectory);
}

public interface ISetupFileSystem
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    string ReadAllText(string path);
    void WriteAllTextAtomic(string path, string contents);
    string ComputeSha256(string path);
    void CreateDirectory(string path);
    void CopyFile(string source, string destination, bool overwrite);
    void MoveDirectory(string source, string destination);
    void DeleteDirectory(string path, bool recursive);
    void DeleteFile(string path);
    void EnsureDirectoryAccess(string path, DirectoryAccessKind accessKind);
    bool CanCreateUnder(string path);
    void ValidateDeploymentPaths(
        DeploymentPaths paths,
        ServiceSnapshot service,
        IReadOnlyList<string> transactionPaths);
    void ValidateRecoveryPaths(
        DeploymentPaths paths,
        ServiceSnapshot currentService,
        ServiceSnapshot previousService,
        bool allowFreshCreatedDataCleanup,
        IReadOnlyList<string> transactionPaths);
}

public enum DirectoryAccessKind
{
    ProgramReadExecute,
    AgentDataModify,
    AdministratorOnly
}

public interface IServiceManager
{
    ServiceSnapshot Capture(string serviceName);
    void Stop(string serviceName, TimeSpan timeout);
    void InstallOrUpdate(
        string serviceName,
        string displayName,
        string binaryPath,
        string accountName);
    void ConfigureRecovery(string serviceName);
    void DisableRecovery(string serviceName);
    void Start(string serviceName, TimeSpan timeout);
    void Restore(string serviceName, ServiceSnapshot snapshot);
}

public interface IFirewallManager
{
    FirewallRuleSnapshot Capture(string ruleName);
    void ApplyViewerRule(string ruleName, int port, string viewerIpv4);
    void RemoveOwnedRule(string ruleName);
    void Restore(FirewallRuleSnapshot snapshot);
    bool IsExactViewerRule(string ruleName, int port, string viewerIpv4);
    FirewallSecurityAssessment AssertSecurityGate(
        int port,
        string agentExecutablePath);
}

public interface IAgentHealthProbe
{
    Task<AgentHealthProbeResult> WaitUntilReadyAsync(
        Uri endpoint,
        string? expectedProductVersion,
        Func<ServiceSnapshot> currentServiceSnapshot,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public enum AgentHealthProbeCode : byte
{
    Ready = 0,
    ServiceUnavailable = 1,
    ServiceInspectionFailed = 2,
    TcpNotListening = 3,
    TcpOwnedByOtherProcess = 4,
    TcpOwnershipQueryFailed = 5,
    HttpsRequestFailed = 6,
    HttpStatusInvalid = 7,
    PayloadTooLarge = 8,
    PayloadInvalid = 9,
    ApiVersionMismatch = 10,
    ProtocolMismatch = 11,
    ProductVersionMismatch = 12,
    DeadlineExceeded = 13
}

public readonly record struct AgentHealthProbeResult(
    bool Ready,
    AgentHealthProbeCode Code,
    bool RestartObserved)
{
    public static AgentHealthProbeResult Success(bool restartObserved) =>
        new(true, AgentHealthProbeCode.Ready, restartObserved);

    public static AgentHealthProbeResult Failure(
        AgentHealthProbeCode code,
        bool restartObserved)
    {
        if (code == AgentHealthProbeCode.Ready)
        {
            throw new ArgumentOutOfRangeException(
                nameof(code),
                "A failed Agent health probe cannot use the Ready code.");
        }

        return new AgentHealthProbeResult(false, code, restartObserved);
    }
}

public interface IAdministratorChecker
{
    bool IsAdministrator();
}

public interface INetworkDiscovery
{
    IReadOnlyList<NetworkCandidate> DiscoverPrivateIpv4Networks();
}

public interface IMachineDeploymentLock
{
    IDisposable Acquire();
}

public static class Ipv4Input
{
    public static bool TryParseStrict(string? value, out IPAddress address)
    {
        address = IPAddress.None;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var pieces = value.Trim().Split('.');
        if (pieces.Length != 4)
        {
            return false;
        }

        var bytes = new byte[4];
        for (var index = 0; index < pieces.Length; index++)
        {
            var piece = pieces[index];
            if (piece.Length is < 1 or > 3 ||
                piece.Length > 1 && piece[0] == '0' ||
                piece.Any(character => character is < '0' or > '9') ||
                !byte.TryParse(piece, out bytes[index]))
            {
                return false;
            }
        }

        address = new IPAddress(bytes);
        return true;
    }

    public static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
               (bytes[0] == 10 ||
                bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                bytes[0] == 192 && bytes[1] == 168);
    }

    public static bool IsPrivateNetwork(IPAddress network, int prefixLength)
    {
        var bytes = network.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        return bytes[0] == 10 && prefixLength >= 8 ||
               bytes[0] == 172 &&
               bytes[1] is >= 16 and <= 31 &&
               prefixLength >= 12 ||
               bytes[0] == 192 &&
               bytes[1] == 168 &&
               prefixLength >= 16;
    }

    internal static bool TryNormalizePrivateCidr(
        string? value,
        out string canonicalCidr)
    {
        canonicalCidr = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var pieces = trimmed.Split('/');
        if (pieces.Length != 2 ||
            pieces[0].Length == 0 ||
            pieces[0] != pieces[0].Trim() ||
            pieces[1].Length == 0 ||
            pieces[1] != pieces[1].Trim() ||
            pieces[1].Length > 1 && pieces[1][0] == '0' ||
            pieces[1].Any(character => character is < '0' or > '9') ||
            !TryParseStrict(pieces[0], out var address) ||
            !int.TryParse(
                pieces[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var prefixLength) ||
            prefixLength is < 0 or > 32)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        var numeric = ((uint)bytes[0] << 24) |
                      ((uint)bytes[1] << 16) |
                      ((uint)bytes[2] << 8) |
                      bytes[3];
        var mask = prefixLength == 0
            ? 0
            : uint.MaxValue << (32 - prefixLength);
        var normalized = numeric & mask;
        var network = new IPAddress(
        [
            (byte)(normalized >> 24),
            (byte)(normalized >> 16),
            (byte)(normalized >> 8),
            (byte)normalized
        ]);
        if (!IsPrivateNetwork(network, prefixLength))
        {
            return false;
        }

        canonicalCidr = $"{network}/{prefixLength}";
        return true;
    }

    internal static bool IsCanonicalPrivateCidr(string? value) =>
        TryNormalizePrivateCidr(value, out var canonicalCidr) &&
        string.Equals(value, canonicalCidr, StringComparison.Ordinal);
}
