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
    public int JournalDeleteFailuresRemaining { get; set; }
    public int SilentJournalDeleteAttemptsRemaining { get; set; }
    public bool RecreateJournalAfterDeleteVerification { get; set; }
    public bool HideJournalAfterDeleteFailureUntilAccessNormalization { get; set; }
    public bool KeepFalseProbeAfterAccessNormalization { get; set; }
    public int JournalDeleteAttempts { get; private set; }
    public int JournalUpgradeWriteFailuresRemaining { get; set; }
    public int RollbackMarkerWriteFailuresRemaining { get; set; }
    public int StagingDirectoryCleanupFailuresRemaining { get; set; }
    public int StagingDirectoryCleanupAttempts { get; private set; }
    public int BackupDirectoryCleanupFailuresRemaining { get; set; }
    public int BackupDirectoryCleanupAttempts { get; private set; }
    public int FailedDirectoryCleanupFailuresRemaining { get; set; }
    public int FailedDirectoryCleanupAttempts { get; private set; }
    public bool HideCleanupDirectoryAfterDeleteFailureUntilAccessNormalization
    {
        get;
        set;
    }
    public int ActivationMoveFailuresRemaining { get; set; }
    public Func<string, string, bool>? MoveFailurePredicate { get; set; }
    public int MoveFailuresRemaining { get; set; }
    private int MatchingAccessRequests { get; set; }
    private string? JournalReappearancePath { get; set; }
    private string? JournalReappearanceContents { get; set; }
    private int JournalMissingChecksBeforeReappearance { get; set; }
    private string? HiddenJournalParentUntilAccessNormalization { get; set; }
    private string? HiddenCleanupDirectoryUntilAccessNormalization { get; set; }

    public bool FileExists(string path)
    {
        if (HiddenJournalParentUntilAccessNormalization is not null &&
            PhysicalSetupFileSystem.SamePath(
                Path.GetDirectoryName(path)!,
                HiddenJournalParentUntilAccessNormalization) &&
            string.Equals(
                Path.GetFileName(path),
                "agent-native-setup-transaction.json",
                StringComparison.Ordinal))
        {
            return false;
        }

        if (JournalReappearancePath is not null &&
            PhysicalSetupFileSystem.SamePath(path, JournalReappearancePath) &&
            !File.Exists(path))
        {
            if (JournalMissingChecksBeforeReappearance > 0)
            {
                JournalMissingChecksBeforeReappearance--;
                return false;
            }

            File.WriteAllText(path, JournalReappearanceContents!);
            JournalReappearancePath = null;
            JournalReappearanceContents = null;
        }

        return _inner.FileExists(path);
    }
    public bool DirectoryExists(string path) =>
        HiddenCleanupDirectoryUntilAccessNormalization is not null &&
        PhysicalSetupFileSystem.SamePath(
            path,
            HiddenCleanupDirectoryUntilAccessNormalization)
            ? false
            : _inner.DirectoryExists(path);
    public string ReadAllText(string path) => _inner.ReadAllText(path);
    public void WriteAllTextAtomic(string path, string contents)
    {
        if (JournalUpgradeWriteFailuresRemaining > 0 &&
            contents.Contains("\"FormatVersion\": 2", StringComparison.Ordinal) &&
            File.Exists(path) &&
            File.ReadAllText(path).Contains("\"FormatVersion\": 1", StringComparison.Ordinal))
        {
            JournalUpgradeWriteFailuresRemaining--;
            throw new IOException("simulated journal format upgrade failure");
        }

        if (RollbackMarkerWriteFailuresRemaining > 0 &&
            contents.Contains("\"Stage\": \"rollback-completed\"", StringComparison.Ordinal))
        {
            RollbackMarkerWriteFailuresRemaining--;
            throw new IOException("simulated rollback marker write failure");
        }

        _inner.WriteAllTextAtomic(path, contents);
    }
    public string ComputeSha256(string path) => _inner.ComputeSha256(path);
    public void CreateDirectory(string path) => _inner.CreateDirectory(path);
    public void CopyFile(string source, string destination, bool overwrite) =>
        _inner.CopyFile(source, destination, overwrite);
    public void MoveDirectory(string source, string destination)
    {
        if (MoveFailuresRemaining > 0 &&
            MoveFailurePredicate?.Invoke(source, destination) == true)
        {
            MoveFailuresRemaining--;
            throw new IOException("simulated directory move failure");
        }

        if (ActivationMoveFailuresRemaining > 0 &&
            Path.GetFileName(source).Contains(".__staging_", StringComparison.Ordinal))
        {
            ActivationMoveFailuresRemaining--;
            throw new IOException("simulated activation move failure");
        }

        _inner.MoveDirectory(source, destination);
    }
    public void DeleteDirectory(string path, bool recursive) =>
        DeleteDirectoryCore(path, recursive);
    public void DeleteFile(string path)
    {
        var isJournal = string.Equals(
            Path.GetFileName(path),
            "agent-native-setup-transaction.json",
            StringComparison.Ordinal);
        if (isJournal)
        {
            JournalDeleteAttempts++;
            if (JournalDeleteFailuresRemaining > 0)
            {
                JournalDeleteFailuresRemaining--;
                if (HideJournalAfterDeleteFailureUntilAccessNormalization)
                {
                    HiddenJournalParentUntilAccessNormalization =
                        Path.GetDirectoryName(path);
                }
                throw new IOException("simulated journal delete failure");
            }

            if (SilentJournalDeleteAttemptsRemaining > 0)
            {
                SilentJournalDeleteAttemptsRemaining--;
                return;
            }

            if (RecreateJournalAfterDeleteVerification)
            {
                RecreateJournalAfterDeleteVerification = false;
                JournalReappearancePath = path;
                JournalReappearanceContents = File.ReadAllText(path);
                // DeploymentJournalStore.Delete and the bounded cleanup helper
                // each verify absence. Recreate for RecoverAsync's final check.
                JournalMissingChecksBeforeReappearance = 2;
            }
        }

        File.Delete(path);
    }
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

        if (HiddenJournalParentUntilAccessNormalization is not null &&
            accessKind == DirectoryAccessKind.AdministratorOnly &&
            PhysicalSetupFileSystem.SamePath(
                path,
                HiddenJournalParentUntilAccessNormalization) &&
            !KeepFalseProbeAfterAccessNormalization)
        {
            HiddenJournalParentUntilAccessNormalization = null;
        }

        if (HiddenCleanupDirectoryUntilAccessNormalization is not null &&
            accessKind == DirectoryAccessKind.AdministratorOnly &&
            PhysicalSetupFileSystem.SamePath(
                path,
                HiddenCleanupDirectoryUntilAccessNormalization) &&
            !KeepFalseProbeAfterAccessNormalization)
        {
            HiddenCleanupDirectoryUntilAccessNormalization = null;
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
        if (Path.GetFileName(path).Contains(".__staging_", StringComparison.Ordinal))
        {
            StagingDirectoryCleanupAttempts++;
            if (StagingDirectoryCleanupFailuresRemaining > 0)
            {
                StagingDirectoryCleanupFailuresRemaining--;
                HideCleanupDirectoryAfterFailure(path);
                throw new IOException("simulated staging directory cleanup failure");
            }
        }

        if (Path.GetFileName(path).Contains(".__backup_", StringComparison.Ordinal))
        {
            BackupDirectoryCleanupAttempts++;
            if (BackupDirectoryCleanupFailuresRemaining > 0)
            {
                BackupDirectoryCleanupFailuresRemaining--;
                HideCleanupDirectoryAfterFailure(path);
                throw new IOException("simulated backup directory cleanup failure");
            }
        }

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

        if (FailedDirectoryCleanupFailuresRemaining > 0 &&
            Path.GetFileName(path).Contains(".__failed_", StringComparison.Ordinal))
        {
            FailedDirectoryCleanupAttempts++;
            FailedDirectoryCleanupFailuresRemaining--;
            HideCleanupDirectoryAfterFailure(path);
            throw new IOException("simulated failed directory cleanup failure");
        }

        if (Path.GetFileName(path).Contains(".__failed_", StringComparison.Ordinal))
        {
            FailedDirectoryCleanupAttempts++;
        }

        _inner.DeleteDirectory(path, recursive);
    }

    private void HideCleanupDirectoryAfterFailure(string path)
    {
        if (HideCleanupDirectoryAfterDeleteFailureUntilAccessNormalization)
        {
            HiddenCleanupDirectoryUntilAccessNormalization = path;
        }
    }
}

internal sealed class FakeAdministratorChecker(bool isAdministrator = true)
    : IAdministratorChecker
{
    public bool IsAdministrator() => isAdministrator;
}

internal sealed class FakeMachineDeploymentLock : IMachineDeploymentLock
{
    public Exception? AcquireException { get; set; }
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
    public int StopFailuresRemaining { get; set; }
    public int StopFailureOccurrence { get; set; } = 1;
    public int RestoreFailuresRemaining { get; set; }
    public Exception? CaptureException { get; set; }
    public Exception? StartException { get; set; }
    public Action? StartCompleted { get; set; }
    private int StopCallCount { get; set; }

    public void SetState(ServiceSnapshot state) =>
        State = Clone(state);

    public ServiceSnapshot Capture(string serviceName)
    {
        Operations.Add("capture");
        if (CaptureException is not null)
        {
            throw CaptureException;
        }

        return Clone(State);
    }

    public void Stop(string serviceName, TimeSpan timeout)
    {
        Operations.Add("stop");
        StopCallCount++;
        if (StopFailuresRemaining > 0 &&
            StopCallCount >= StopFailureOccurrence)
        {
            StopFailuresRemaining--;
            throw new InvalidOperationException("simulated service stop failure");
        }

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

    public void ConfigureRecovery(string serviceName)
    {
        Operations.Add("recovery");
        State = State with
        {
            Recovery = WindowsServiceManager.CreateAutomaticRecoveryPolicy()
        };
    }

    public void DisableRecovery(string serviceName)
    {
        Operations.Add("recovery-disabled");
        State = State with
        {
            Recovery = WindowsServiceManager.CreateDisabledRecoveryPolicy()
        };
    }

    public void Start(string serviceName, TimeSpan timeout)
    {
        Operations.Add("start");
        if (StartException is not null)
        {
            throw StartException;
        }

        State = State with { Running = true };
        State = State with { ProcessId = 4321 };
        StartCompleted?.Invoke();
    }

    public void Restore(string serviceName, ServiceSnapshot snapshot)
    {
        Operations.Add("restore");
        if (RestoreFailuresRemaining > 0)
        {
            RestoreFailuresRemaining--;
            throw new InvalidOperationException("simulated service restore failure");
        }

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
    public FirewallSecurityAssessment SecurityAssessment { get; set; } =
        FirewallSecurityAssessment.Safe;
    public Func<int, FirewallRuleSnapshot>? AppliedRuleReadback { get; set; }
    public int AppliedRuleCaptureCount { get; private set; }
    public ManualResetEventSlim AppliedRuleCaptured { get; } = new();
    public HashSet<string> RestoreFailureRuleNames { get; } =
        new(StringComparer.Ordinal);
    private bool _viewerRuleApplied;

    public FirewallRuleSnapshot Capture(string ruleName)
    {
        Operations.Add($"capture:{ruleName}");
        if (_viewerRuleApplied &&
            string.Equals(
                ruleName,
                SetupConstants.FirewallRuleName,
                StringComparison.Ordinal) &&
            AppliedRuleReadback is not null)
        {
            AppliedRuleCaptureCount++;
            AppliedRuleCaptured.Set();
            State = AppliedRuleReadback(AppliedRuleCaptureCount);
        }

        return string.Equals(State.Name, ruleName, StringComparison.Ordinal)
            ? State
            : FirewallRuleSnapshot.Missing(ruleName);
    }

    public void ApplyViewerRule(string ruleName, int port, string viewerIpv4)
    {
        Operations.Add("apply");
        _viewerRuleApplied = true;
        AppliedRuleCaptureCount = 0;
        State = new FirewallRuleSnapshot(
            true,
            ruleName,
            "test",
            true,
            1,
            1,
            6,
            port.ToString(),
            viewerIpv4.Contains(',', StringComparison.Ordinal) ||
            viewerIpv4.Contains('/', StringComparison.Ordinal)
                ? viewerIpv4
                : $"{viewerIpv4}/32",
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
        if (RestoreFailureRuleNames.Remove(snapshot.Name))
        {
            throw new InvalidOperationException(
                "simulated firewall restore failure");
        }

        _viewerRuleApplied = false;
        if (snapshot.Name == SetupConstants.FirewallRuleName ||
            !State.Exists)
        {
            State = snapshot;
        }
    }

    public bool IsExactViewerRule(string ruleName, int port, string viewerIpv4) =>
        string.Equals(
                viewerIpv4,
                SetupConstants.PrivateNetworkFirewallRemoteAddresses,
                StringComparison.Ordinal)
            ? FirewallRuleVerifier.EvaluatePrivateNetworks(
                Capture(ruleName),
                port).IsExact
            : FirewallRuleVerifier.Evaluate(
                Capture(ruleName),
                port,
                viewerIpv4).IsExact;

    public FirewallSecurityAssessment AssertSecurityGate(
        int port,
        string agentExecutablePath)
    {
        Operations.Add("security-gate");
        if (SecurityGateException is not null)
        {
            throw SecurityGateException;
        }

        return SecurityAssessment;
    }
}

internal sealed class FakeHealthProbe(
    bool ready,
    Action? beforeResult = null,
    AgentHealthProbeCode failureCode =
        AgentHealthProbeCode.DeadlineExceeded,
    bool serviceRunningObserved = false,
    bool listenerOwnedObserved = false,
    int httpAttemptCount = 0,
    AgentHealthTransportPhase lastTransportPhase =
        AgentHealthTransportPhase.NotStarted) : IAgentHealthProbe
{
    public Task<AgentHealthProbeResult> WaitUntilReadyAsync(
        Uri endpoint,
        string? expectedProductVersion,
        Func<ServiceSnapshot> currentServiceSnapshot,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        _ = currentServiceSnapshot();
        beforeResult?.Invoke();
        return Task.FromResult(
            ready
                ? AgentHealthProbeResult.Success(restartObserved: false)
                : AgentHealthProbeResult.Failure(
                    failureCode,
                    restartObserved: false,
                    serviceRunningObserved,
                    listenerOwnedObserved,
                    httpAttemptCount,
                    lastTransportPhase));
    }
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
