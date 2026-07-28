using SamsungSwitchWatch.Agent.Setup.Deployment;
using SamsungSwitchWatch.Agent.Setup.Infrastructure;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

internal sealed class TestFileSystem : ISetupFileSystem
{
    private readonly PhysicalSetupFileSystem _inner = new();

    public List<(string Path, DirectoryAccessKind Kind)> AccessRequests { get; } = [];
    public bool FailBackupCleanup { get; set; }
    public string? FreshDataDirectory { get; set; }
    public int DataCleanupFailuresRemaining { get; set; }
    public SetupException? PathValidationException { get; set; }
    public string? AccessFailurePath { get; set; }
    public DirectoryAccessKind? AccessFailureKind { get; set; }
    public int AccessFailureOccurrence { get; set; } = 1;
    private int MatchingAccessRequests { get; set; }

    public bool FileExists(string path) => _inner.FileExists(path);
    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);
    public string ReadAllText(string path) => _inner.ReadAllText(path);
    public void WriteAllTextAtomic(string path, string contents) =>
        _inner.WriteAllTextAtomic(path, contents);
    public string ComputeSha256(string path) => _inner.ComputeSha256(path);
    public void CreateDirectory(string path) => _inner.CreateDirectory(path);
    public void CopyFile(string source, string destination, bool overwrite) =>
        _inner.CopyFile(source, destination, overwrite);
    public void MoveDirectory(string source, string destination) =>
        _inner.MoveDirectory(source, destination);
    public void DeleteDirectory(string path, bool recursive) =>
        DeleteDirectoryCore(path, recursive);
    public void DeleteFile(string path) => File.Delete(path);
    public bool CanCreateUnder(string path) => true;
    public void EnsureDirectoryAccess(string path, DirectoryAccessKind accessKind)
    {
        AccessRequests.Add((path, accessKind));
        if (AccessFailurePath is not null &&
            PhysicalSetupFileSystem.SamePath(path, AccessFailurePath) &&
            AccessFailureKind == accessKind &&
            ++MatchingAccessRequests == AccessFailureOccurrence)
        {
            throw new IOException("simulated directory access failure");
        }
    }
    public void ValidateDeploymentPaths(
        DeploymentPaths paths,
        ServiceSnapshot service,
        IReadOnlyList<string> transactionPaths)
    {
        if (PathValidationException is not null)
        {
            throw PathValidationException;
        }
    }
    public void ValidateRecoveryPaths(
        DeploymentPaths paths,
        ServiceSnapshot currentService,
        ServiceSnapshot previousService,
        bool allowFreshCreatedDataCleanup,
        IReadOnlyList<string> transactionPaths)
    {
        if (PathValidationException is not null)
        {
            throw PathValidationException;
        }
    }

    private void DeleteDirectoryCore(string path, bool recursive)
    {
        if (DataCleanupFailuresRemaining > 0 &&
            FreshDataDirectory is not null &&
            PhysicalSetupFileSystem.SamePath(path, FreshDataDirectory))
        {
            DataCleanupFailuresRemaining--;
            throw new IOException("simulated data cleanup failure");
        }

        if (FailBackupCleanup &&
            Path.GetFileName(path).Contains(".__backup_", StringComparison.Ordinal))
        {
            throw new IOException("simulated cleanup failure");
        }

        _inner.DeleteDirectory(path, recursive);
    }
}

internal sealed class FakeAdministratorChecker(bool isAdministrator = true)
    : IAdministratorChecker
{
    public bool IsAdministrator() => isAdministrator;
}

internal sealed class FakeMachineDeploymentLock : IMachineDeploymentLock
{
    public SetupException? AcquireException { get; set; }
    public int AcquireCount { get; private set; }
    public int ReleaseCount { get; private set; }

    public IDisposable Acquire()
    {
        AcquireCount++;
        if (AcquireException is not null)
        {
            throw AcquireException;
        }

        return new Lease(this);
    }

    private sealed class Lease(FakeMachineDeploymentLock owner) : IDisposable
    {
        private FakeMachineDeploymentLock? _owner = owner;

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _owner, null);
            if (value is not null)
            {
                value.ReleaseCount++;
            }
        }
    }
}

internal sealed class FakeServiceManager(ServiceSnapshot initial) : IServiceManager
{
    public ServiceSnapshot State { get; private set; } = Clone(initial);
    public List<string> Operations { get; } = [];
    public ServiceSnapshot? InstalledState { get; private set; }

    public ServiceSnapshot Capture(string serviceName)
    {
        Operations.Add("capture");
        return Clone(State);
    }

    public void Stop(string serviceName, TimeSpan timeout)
    {
        Operations.Add("stop");
        State = State with { Running = false, ProcessId = 0 };
    }

    public void InstallOrUpdate(
        string serviceName,
        string displayName,
        string binaryPath,
        string accountName)
    {
        Operations.Add("install");
        State = new ServiceSnapshot(
            true,
            false,
            binaryPath,
            2,
            accountName,
            SetupConstants.ServiceDisplayName,
            "Windowless Samsung switch Telnet execution Agent",
            1,
            new ServiceRecoverySnapshot(
                86400,
                true,
                string.Empty,
                string.Empty,
                [
                    new ServiceFailureActionSnapshot(1, 5000),
                    new ServiceFailureActionSnapshot(1, 15000),
                    new ServiceFailureActionSnapshot(1, 60000)
                ]),
            [1, 2, 3],
            4321);
        InstalledState = Clone(State);
    }

    public void ConfigureRecovery(string serviceName) => Operations.Add("recovery");

    public void Start(string serviceName, TimeSpan timeout)
    {
        Operations.Add("start");
        State = State with { Running = true };
        State = State with { ProcessId = 4321 };
    }

    public void Restore(string serviceName, ServiceSnapshot snapshot)
    {
        Operations.Add("restore");
        State = Clone(snapshot);
    }

    private static ServiceSnapshot Clone(ServiceSnapshot value) =>
        value with
        {
            SecurityDescriptor = value.SecurityDescriptor?.ToArray()
        };
}

internal sealed class FakeFirewallManager(FirewallRuleSnapshot initial) : IFirewallManager
{
    public FirewallRuleSnapshot State { get; private set; } = initial;
    public List<string> Operations { get; } = [];
    public SetupException? SecurityGateException { get; set; }

    public FirewallRuleSnapshot Capture(string ruleName)
    {
        Operations.Add($"capture:{ruleName}");
        return string.Equals(State.Name, ruleName, StringComparison.Ordinal)
            ? State
            : FirewallRuleSnapshot.Missing(ruleName);
    }

    public void ApplyViewerRule(string ruleName, int port, string viewerIpv4)
    {
        Operations.Add("apply");
        State = new FirewallRuleSnapshot(
            true,
            ruleName,
            "test",
            true,
            1,
            1,
            6,
            port.ToString(),
            $"{viewerIpv4}/32",
            3,
            "All",
            false,
            string.Empty);
    }

    public void RemoveOwnedRule(string ruleName)
    {
        Operations.Add($"remove:{ruleName}");
        if (string.Equals(State.Name, ruleName, StringComparison.Ordinal))
        {
            State = FirewallRuleSnapshot.Missing(ruleName);
        }
    }

    public void Restore(FirewallRuleSnapshot snapshot)
    {
        Operations.Add($"restore:{snapshot.Name}");
        if (snapshot.Name == SetupConstants.FirewallRuleName ||
            !State.Exists)
        {
            State = snapshot;
        }
    }

    public bool IsExactViewerRule(string ruleName, int port, string viewerIpv4) =>
        State.Exists &&
        State.Name == ruleName &&
        State.LocalPorts == port.ToString() &&
        State.RemoteAddresses == $"{viewerIpv4}/32" &&
        State.Profiles == 3;

    public void AssertSecurityGate(int port, string agentExecutablePath)
    {
        Operations.Add("security-gate");
        if (SecurityGateException is not null)
        {
            throw SecurityGateException;
        }
    }
}

internal sealed class FakeHealthProbe(bool ready) : IAgentHealthProbe
{
    public Task<bool> WaitUntilReadyAsync(
        Uri endpoint,
        string? expectedProductVersion,
        int expectedProcessId,
        TimeSpan timeout,
        CancellationToken cancellationToken) =>
        Task.FromResult(ready);
}

internal sealed class TemporaryFolder : IDisposable
{
    public TemporaryFolder()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"ssw-native-setup-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) =>
        parts.Aggregate(Path, System.IO.Path.Combine);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Test cleanup is best effort only.
        }
    }
}
