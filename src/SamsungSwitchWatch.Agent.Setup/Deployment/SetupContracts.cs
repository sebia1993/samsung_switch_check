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
    public const string AlreadyRunning = "SETUP_ALREADY_RUNNING";
    public const string Cancelled = "SETUP_CANCELLED";
    public const string Unexpected = "SETUP_UNEXPECTED";
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

public sealed record SetupOperationResult(
    bool Succeeded,
    string Code,
    string Message,
    IReadOnlyList<SetupStepResult> Steps)
{
    public static SetupOperationResult Failure(
        string code,
        string message,
        IReadOnlyList<SetupStepResult> steps) =>
        new(false, code, message, steps);

    public static SetupOperationResult Success(
        string message,
        IReadOnlyList<SetupStepResult> steps) =>
        new(true, SetupErrorCodes.Ok, message, steps);
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
    Task<bool> WaitUntilReadyAsync(
        Uri endpoint,
        string? expectedProductVersion,
        int expectedProcessId,
        TimeSpan timeout,
        CancellationToken cancellationToken);
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
