using System.Text.Json.Nodes;
using SamsungSwitchWatch.Agent.Setup.Deployment;
using SamsungSwitchWatch.Agent.Setup.Infrastructure;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class AgentDeploymentOrchestratorTests
{
    [Fact]
    public async Task DeployAsync_UpgradePreservesIdentityAndValidatedConfiguration()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var orchestrator = fixture.CreateOrchestrator(ready: true);

        var result = await orchestrator.DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            "new-agent",
            File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.Equal(
            "identity-secret",
            File.ReadAllText(Path.Combine(fixture.Paths.DataDirectory, "https-certificate.pfx.dpapi")));
        var agent = JsonNode.Parse(
            File.ReadAllText(fixture.Paths.ProductionConfigurationPath))!["Agent"]!;
        Assert.Equal("preserved-agent", agent["AgentId"]!.GetValue<string>());
        Assert.Equal(90, agent["Telnet"]!["MaxSessionSeconds"]!.GetValue<int>());
        Assert.Equal(45, agent["RateLimitPerMinute"]!.GetValue<int>());
        Assert.Equal(
            "192.168.1.20",
            agent["AllowedViewerIpv4"]!.GetValue<string>());
        Assert.Equal(
            "192.168.40.0/24",
            agent["AllowedTargetCidrs"]![0]!.GetValue<string>());
        Assert.True(fixture.Services.State.Running);
        Assert.Contains("--service", fixture.Services.State.BinaryPath);
        Assert.Equal("192.168.1.20/32", fixture.Firewall.State.RemoteAddresses);
        Assert.Contains(
            fixture.FileSystem.AccessRequests,
            request => request.Kind == DirectoryAccessKind.AgentDataModify);
    }

    [Fact]
    public async Task DeployAsync_HealthFailureRestoresUpgradeFilesServiceFirewallAndIdentity()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var oldService = fixture.Services.State;
        var oldFirewall = fixture.Firewall.State;
        var orchestrator = fixture.CreateOrchestrator(ready: false);

        var result = await orchestrator.DeployAsync(
            new SetupRequest("192.168.1.20", ["10.20.0.0/16"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.HealthFailed, result.Code);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.Contains(
            "\"AgentId\": \"preserved-agent\"",
            File.ReadAllText(fixture.Paths.ProductionConfigurationPath));
        Assert.Equal(
            "identity-secret",
            File.ReadAllText(Path.Combine(fixture.Paths.DataDirectory, "https-certificate.pfx.dpapi")));
        Assert.Equal(oldService.BinaryPath, fixture.Services.State.BinaryPath);
        Assert.Equal(oldService.Running, fixture.Services.State.Running);
        Assert.Equal(oldService.DisplayName, fixture.Services.State.DisplayName);
        Assert.Equal(oldService.Description, fixture.Services.State.Description);
        Assert.Equal(oldService.ServiceSidType, fixture.Services.State.ServiceSidType);
        Assert.Equal(oldService.Recovery, fixture.Services.State.Recovery);
        Assert.Equal(oldFirewall, fixture.Firewall.State);
        Assert.Contains(
            result.Steps,
            step => step.Code == "ROLLBACK_COMPLETED");
    }

    [Fact]
    public async Task DeployAsync_FreshHealthFailureRemovesNewProgramAndService()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateFreshFixture(folder);
        var orchestrator = fixture.CreateOrchestrator(ready: false);

        var result = await orchestrator.DeployAsync(
            new SetupRequest("10.1.1.20", ["10.30.0.0/16"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(fixture.Paths.InstallDirectory));
        Assert.False(Directory.Exists(fixture.Paths.DataDirectory));
        Assert.False(fixture.Services.State.Exists);
        Assert.False(fixture.Firewall.State.Exists);
    }

    [Fact]
    public async Task DeployAsync_FreshRollbackCleanupCanResumeAndNextDeploySucceeds()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateFreshFixture(folder);
        fixture.FileSystem.FreshDataDirectory = fixture.Paths.DataDirectory;
        fixture.FileSystem.DataCleanupFailuresRemaining = 1;

        var first = await fixture.CreateOrchestrator(ready: false).DeployAsync(
            new SetupRequest("10.1.1.20", ["10.30.0.0/16"]),
            CancellationToken.None);

        Assert.False(first.Succeeded);
        Assert.Equal(SetupErrorCodes.RollbackFailed, first.Code);
        Assert.True(Directory.Exists(fixture.Paths.DataDirectory));
        Assert.True(new DeploymentJournalStore(
            fixture.FileSystem,
            fixture.Paths).Exists);

        var second = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("10.1.1.20", ["10.30.0.0/16"]),
            CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.True(fixture.Services.State.Running);
        Assert.False(new DeploymentJournalStore(
            fixture.FileSystem,
            fixture.Paths).Exists);
    }

    [Fact]
    public async Task DeployAsync_RestoredBackupAclFailureCanResumeAndNextDeploySucceeds()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.FileSystem.AccessFailurePath = fixture.Paths.InstallDirectory;
        fixture.FileSystem.AccessFailureKind = DirectoryAccessKind.ProgramReadExecute;
        fixture.FileSystem.AccessFailureOccurrence = 2;

        var first = await fixture.CreateOrchestrator(ready: false).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(first.Succeeded);
        Assert.Equal(SetupErrorCodes.RollbackFailed, first.Code);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(new DeploymentJournalStore(
            fixture.FileSystem,
            fixture.Paths).Exists);

        var second = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal("new-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(fixture.Services.State.Running);
        Assert.False(new DeploymentJournalStore(
            fixture.FileSystem,
            fixture.Paths).Exists);
    }

    [Fact]
    public async Task DeployAsync_RollbackJournalDeleteFailureResumesWithoutMovingRestoredInstall()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.FileSystem.JournalDeleteFailuresRemaining = 2;
        var journalStore = new DeploymentJournalStore(
            fixture.FileSystem,
            fixture.Paths);

        var first = await fixture.CreateOrchestrator(ready: false).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(first.Succeeded);
        Assert.Equal(SetupErrorCodes.HealthFailed, first.Code);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(journalStore.Exists);
        Assert.Equal("rollback-completed", journalStore.Read().Stage);
        Assert.Equal(
            DeploymentJournalStore.CurrentFormatVersion,
            journalStore.Read().FormatVersion);
        Assert.Empty(Directory.GetDirectories(
            Path.GetDirectoryName(fixture.Paths.InstallDirectory)!,
            $"{Path.GetFileName(fixture.Paths.InstallDirectory)}.__failed_*"));

        var second = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(second.Succeeded);
        Assert.Equal(SetupErrorCodes.RecoveryRequired, second.Code);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(journalStore.Exists);
        Assert.Equal("rollback-completed", journalStore.Read().Stage);

        var third = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.True(third.Succeeded);
        Assert.Equal("new-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(fixture.Services.State.Running);
        Assert.False(journalStore.Exists);
    }

    [Fact]
    public async Task DeployAsync_RollbackMarkerWriteFailurePreservesFailedDirectoryForRecovery()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.FileSystem.RollbackMarkerWriteFailuresRemaining = 1;
        var journalStore = new DeploymentJournalStore(
            fixture.FileSystem,
            fixture.Paths);

        var first = await fixture.CreateOrchestrator(ready: false).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(first.Succeeded);
        Assert.Equal(SetupErrorCodes.RollbackFailed, first.Code);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(journalStore.Exists);
        Assert.NotEqual("rollback-completed", journalStore.Read().Stage);
        Assert.Single(Directory.GetDirectories(
            Path.GetDirectoryName(fixture.Paths.InstallDirectory)!,
            $"{Path.GetFileName(fixture.Paths.InstallDirectory)}.__failed_*"));

        var second = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal("new-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.False(journalStore.Exists);
    }

    [Fact]
    public async Task DeployAsync_ActivationAndMarkerWriteFailuresPreserveStagingForSafeResume()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.FileSystem.ActivationMoveFailuresRemaining = 1;
        fixture.FileSystem.RollbackMarkerWriteFailuresRemaining = 1;
        var journalStore = new DeploymentJournalStore(
            fixture.FileSystem,
            fixture.Paths);
        var transactionParent = Path.GetDirectoryName(fixture.Paths.InstallDirectory)!;
        var transactionPrefix = Path.GetFileName(fixture.Paths.InstallDirectory);

        var first = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(first.Succeeded);
        Assert.Equal(SetupErrorCodes.RollbackFailed, first.Code);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(journalStore.Exists);
        Assert.Single(Directory.GetDirectories(
            transactionParent,
            $"{transactionPrefix}.__staging_*"));
        Assert.Empty(Directory.GetDirectories(
            transactionParent,
            $"{transactionPrefix}.__backup_*"));
        Assert.Empty(Directory.GetDirectories(
            transactionParent,
            $"{transactionPrefix}.__failed_*"));

        var second = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal("new-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(fixture.Services.State.Running);
        Assert.False(journalStore.Exists);
    }

    [Fact]
    public async Task DeployAsync_RollbackMarkerCleanupFailureResumesAndNextDeploySucceeds()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.FileSystem.FailedDirectoryCleanupFailuresRemaining = 1;
        var journalStore = new DeploymentJournalStore(
            fixture.FileSystem,
            fixture.Paths);

        var first = await fixture.CreateOrchestrator(ready: false).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(first.Succeeded);
        Assert.Equal(SetupErrorCodes.RollbackFailed, first.Code);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(journalStore.Exists);
        Assert.Equal("rollback-completed", journalStore.Read().Stage);
        Assert.Single(Directory.GetDirectories(
            Path.GetDirectoryName(fixture.Paths.InstallDirectory)!,
            $"{Path.GetFileName(fixture.Paths.InstallDirectory)}.__failed_*"));

        var second = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal("new-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(fixture.Services.State.Running);
        Assert.False(journalStore.Exists);
    }

    [Fact]
    public async Task DeployAsync_BackupCleanupFailureKeepsCommittedInstallSuccessful()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.FileSystem.FailBackupCleanup = true;
        var orchestrator = fixture.CreateOrchestrator(ready: true);

        var result = await orchestrator.DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.50.0/24"]),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("new-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(fixture.Services.State.Running);
        Assert.Contains(
            result.Steps,
            step => step.Code == "BACKUP_CLEANUP_PENDING");
    }

    [Fact]
    public async Task Diagnostics_DoesNotMutateServiceFirewallOrFileAccess()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateFreshFixture(folder);
        var diagnostics = new SetupDiagnosticsService(
            new AgentPackageValidator(fixture.FileSystem),
            fixture.FileSystem,
            fixture.Services,
            fixture.Firewall,
            new FakeHealthProbe(true),
            new FakeAdministratorChecker(),
            fixture.Paths);

        var result = await diagnostics.RunAsync(
            new SetupRequest("10.1.1.20", ["10.30.0.0/16"]),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.DoesNotContain(
            fixture.Services.Operations,
            operation => operation is "install" or "start" or "stop" or "restore");
        Assert.DoesNotContain(
            fixture.Firewall.Operations,
            operation => operation is "apply" || operation.StartsWith("remove:", StringComparison.Ordinal));
        Assert.Empty(fixture.FileSystem.AccessRequests);
        Assert.False(Directory.Exists(fixture.Paths.InstallDirectory));
        var pathStep = Assert.Single(result.Steps, step => step.Code == "PATHS_READY");
        Assert.Contains("실제 쓰기 권한", pathStep.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Diagnostics_ReportsExistingViewerRuleMismatchWithoutBlockingRepair()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var diagnostics = new SetupDiagnosticsService(
            new AgentPackageValidator(fixture.FileSystem),
            fixture.FileSystem,
            fixture.Services,
            fixture.Firewall,
            new FakeHealthProbe(true),
            new FakeAdministratorChecker(),
            fixture.Paths);

        var result = await diagnostics.RunAsync(
            new SetupRequest("192.168.1.99", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains(
            result.Steps,
            step =>
                step.Code == "FIREWALL_UPDATE_REQUIRED" &&
                step.State == SetupStepState.Information);
        Assert.DoesNotContain("apply", fixture.Firewall.Operations);
    }

    [Fact]
    public async Task Diagnostics_GenericFirewallOverlapWarnsWithoutMutation()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateFreshFixture(folder);
        fixture.Firewall.SecurityAssessment = OverlapAssessment();
        var diagnostics = new SetupDiagnosticsService(
            new AgentPackageValidator(fixture.FileSystem),
            fixture.FileSystem,
            fixture.Services,
            fixture.Firewall,
            new FakeHealthProbe(true),
            new FakeAdministratorChecker(),
            fixture.Paths);

        var result = await diagnostics.RunAsync(
            new SetupRequest("10.1.1.20", ["10.30.0.0/16"]),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        var warning = Assert.Single(
            result.Steps,
            step => step.Code == WindowsFirewallManager.FirewallOverlapWarningCode);
        Assert.Equal(SetupStepState.Warning, warning.State);
        Assert.Contains("변경하지 않으며", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            fixture.Firewall.Operations,
            operation => operation == "apply" ||
                         operation.StartsWith("remove:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeployAsync_GenericFirewallOverlapSucceedsAndKeepsWarning()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateFreshFixture(folder);
        fixture.Firewall.SecurityAssessment = OverlapAssessment();

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("10.1.1.20", ["10.30.0.0/16"]),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains(
            result.Steps,
            step =>
                step.Code == WindowsFirewallManager.FirewallOverlapWarningCode &&
                step.State == SetupStepState.Warning);
        Assert.True(fixture.Services.State.Running);
        Assert.Contains("apply", fixture.Firewall.Operations);
        Assert.DoesNotContain(
            fixture.Firewall.Operations,
            operation => operation.Contains("Company TCP 18443", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeployAsync_FirewallGateFailureStopsBeforeServiceOrFileMutation()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateFreshFixture(folder);
        fixture.Firewall.SecurityGateException = new SetupException(
            SetupErrorCodes.FirewallFailed,
            "unsafe firewall");
        var orchestrator = fixture.CreateOrchestrator(ready: true);

        var result = await orchestrator.DeployAsync(
            new SetupRequest("10.1.1.20", ["10.30.0.0/16"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.FirewallFailed, result.Code);
        Assert.False(Directory.Exists(fixture.Paths.InstallDirectory));
        Assert.DoesNotContain("install", fixture.Services.Operations);
        Assert.DoesNotContain("apply", fixture.Firewall.Operations);
    }

    [Fact]
    public async Task DeployAsync_MalformedExistingConfigurationStopsBeforeMutation()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        File.WriteAllText(fixture.Paths.ProductionConfigurationPath, "{ malformed");
        var orchestrator = fixture.CreateOrchestrator(ready: true);

        var result = await orchestrator.DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.ConfigurationInvalid, result.Code);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.DoesNotContain("stop", fixture.Services.Operations);
        Assert.DoesNotContain("install", fixture.Services.Operations);
        Assert.DoesNotContain("apply", fixture.Firewall.Operations);
    }

    [Fact]
    public async Task DeployAsync_RecoversPendingBackupBeforeValidatingNewInputOrPackage()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var previousService = fixture.Services.State;
        var previousFirewall = fixture.Firewall.State;
        var transactionId = new string('b', 32);
        var staging = $"{fixture.Paths.InstallDirectory}.__staging_{transactionId}";
        var backup = $"{fixture.Paths.InstallDirectory}.__backup_{transactionId}";
        var failed = $"{fixture.Paths.InstallDirectory}.__failed_{transactionId}";
        Directory.CreateDirectory(staging);
        fixture.Services.Stop(SetupConstants.ServiceName, TimeSpan.Zero);
        Directory.Move(fixture.Paths.InstallDirectory, backup);
        new DeploymentJournalStore(fixture.FileSystem, fixture.Paths).Write(
            new DeploymentJournal(
                1,
                transactionId,
                "backup-move-pending",
                "0.10.0-poc",
                staging,
                backup,
                failed,
                true,
                true,
                false,
                true,
                false,
                previousService,
                previousFirewall,
                FirewallRuleSnapshot.Missing(SetupConstants.LegacyFirewallRuleName)));
        File.Delete(Path.Combine(
            fixture.Paths.PackageDirectory,
            SetupConstants.ManifestFileName));
        var orchestrator = fixture.CreateOrchestrator(ready: true);

        var result = await orchestrator.DeployAsync(
            new SetupRequest(string.Empty, []),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.ViewerIpInvalid, result.Code);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(fixture.Services.State.Running);
        Assert.False(new DeploymentJournalStore(fixture.FileSystem, fixture.Paths).Exists);
        Assert.Contains(
            result.Steps,
            step => step.Code == "ROLLBACK_COMPLETED");
    }

    [Theory]
    [InlineData("unknown-stage", true, true)]
    [InlineData("rollback-completed", false, true)]
    public async Task DeployAsync_InvalidPendingJournalStatePreservesJournalAndInstall(
        string stage,
        bool mutationStarted,
        bool stagingActivated)
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var journalStore = WritePendingJournal(
            fixture,
            stage,
            mutationStarted,
            installMovedToBackup: true,
            stagingActivated);

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RecoveryRequired, result.Code);
        Assert.True(journalStore.Exists);
        Assert.Equal(stage, journalStore.Read().Stage);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.DoesNotContain("stop", fixture.Services.Operations);
        Assert.DoesNotContain("restore", fixture.Services.Operations);
    }

    [Fact]
    public async Task DeployAsync_LegacyRollbackMarkerIsRejectedBeforeMutation()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var journalStore = WritePendingJournal(
            fixture,
            "rollback-completed",
            mutationStarted: true,
            installMovedToBackup: true,
            stagingActivated: true);

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RecoveryRequired, result.Code);
        Assert.True(journalStore.Exists);
        Assert.Equal(
            DeploymentJournalStore.LegacyFormatVersion,
            journalStore.Read().FormatVersion);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.DoesNotContain("stop", fixture.Services.Operations);
        Assert.DoesNotContain("restore", fixture.Services.Operations);
    }

    [Fact]
    public async Task DeployAsync_LegacyPendingRecoveryUpgradesJournalBeforeRollback()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.FileSystem.JournalDeleteFailuresRemaining = 1;
        var journalStore = WritePendingJournal(
            fixture,
            "service-stop-pending",
            mutationStarted: true,
            installMovedToBackup: false,
            stagingActivated: false);

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RecoveryRequired, result.Code);
        Assert.True(journalStore.Exists);
        Assert.Equal(
            DeploymentJournalStore.CurrentFormatVersion,
            journalStore.Read().FormatVersion);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
    }

    [Fact]
    public async Task DeployAsync_LegacyJournalUpgradeFailurePreservesStateBeforeMutation()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.FileSystem.JournalUpgradeWriteFailuresRemaining = 1;
        var journalStore = WritePendingJournal(
            fixture,
            "service-stop-pending",
            mutationStarted: true,
            installMovedToBackup: false,
            stagingActivated: false);

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RecoveryRequired, result.Code);
        Assert.True(journalStore.Exists);
        Assert.Equal(
            DeploymentJournalStore.LegacyFormatVersion,
            journalStore.Read().FormatVersion);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.DoesNotContain("stop", fixture.Services.Operations);
        Assert.DoesNotContain("restore", fixture.Services.Operations);
    }

    [Fact]
    public async Task DeployAsync_RollbackMarkerWithStagingAndFailedRemnantsFailsClosed()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var journalStore = WritePendingJournal(
            fixture,
            "rollback-completed",
            mutationStarted: true,
            installMovedToBackup: true,
            stagingActivated: true,
            formatVersion: DeploymentJournalStore.CurrentFormatVersion);
        var pending = journalStore.Read();
        Directory.CreateDirectory(pending.StagingDirectory);
        Directory.CreateDirectory(pending.FailedDirectory);

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RecoveryRequired, result.Code);
        Assert.True(journalStore.Exists);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(Directory.Exists(pending.StagingDirectory));
        Assert.True(Directory.Exists(pending.FailedDirectory));
        Assert.DoesNotContain("stop", fixture.Services.Operations);
        Assert.DoesNotContain("restore", fixture.Services.Operations);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DeployAsync_ContradictoryRollbackRemnantFailsBeforeServiceMutation(
        bool createBackup,
        bool createFailed)
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var journalStore = WritePendingJournal(
            fixture,
            "service-stop-pending",
            mutationStarted: true,
            installMovedToBackup: false,
            stagingActivated: false,
            formatVersion: DeploymentJournalStore.CurrentFormatVersion);
        var pending = journalStore.Read();
        Directory.CreateDirectory(
            createBackup
                ? pending.BackupDirectory
                : pending.FailedDirectory);

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RecoveryRequired, result.Code);
        Assert.True(journalStore.Exists);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.Equal(createBackup, Directory.Exists(pending.BackupDirectory));
        Assert.Equal(createFailed, Directory.Exists(pending.FailedDirectory));
        Assert.DoesNotContain("stop", fixture.Services.Operations);
        Assert.DoesNotContain("restore", fixture.Services.Operations);
    }

    [Fact]
    public async Task DeployAsync_ActiveRollbackWithStagingAndFailedRemnantsFailsBeforeMutation()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var journalStore = WritePendingJournal(
            fixture,
            "activation-pending",
            mutationStarted: true,
            installMovedToBackup: true,
            stagingActivated: true,
            formatVersion: DeploymentJournalStore.CurrentFormatVersion);
        var pending = journalStore.Read();
        Directory.CreateDirectory(pending.StagingDirectory);
        Directory.CreateDirectory(pending.FailedDirectory);

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RecoveryRequired, result.Code);
        Assert.True(journalStore.Exists);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(Directory.Exists(pending.StagingDirectory));
        Assert.True(Directory.Exists(pending.FailedDirectory));
        Assert.DoesNotContain("stop", fixture.Services.Operations);
        Assert.DoesNotContain("restore", fixture.Services.Operations);
    }

    [Fact]
    public async Task DeployAsync_PostDataStageWithoutDataDecisionFailsBeforeMutation()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateFreshFixture(folder);
        var journalStore = WritePendingJournal(
            fixture,
            "service-configured",
            mutationStarted: true,
            installMovedToBackup: false,
            stagingActivated: true,
            formatVersion: DeploymentJournalStore.CurrentFormatVersion,
            dataDirectoryExistedBefore: false,
            dataDirectoryCreated: false);

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RecoveryRequired, result.Code);
        Assert.True(journalStore.Exists);
        Assert.DoesNotContain("stop", fixture.Services.Operations);
        Assert.DoesNotContain("restore", fixture.Services.Operations);
    }

    [Fact]
    public async Task DeployAsync_PreDataRollbackMarkerRejectsUnexpectedDataDirectory()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateFreshFixture(folder);
        Directory.CreateDirectory(fixture.Paths.DataDirectory);
        var journalStore = WritePendingJournal(
            fixture,
            "rollback-completed",
            mutationStarted: true,
            installMovedToBackup: false,
            stagingActivated: true,
            formatVersion: DeploymentJournalStore.CurrentFormatVersion,
            dataDirectoryExistedBefore: false,
            dataDirectoryCreated: false);

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RecoveryRequired, result.Code);
        Assert.True(journalStore.Exists);
        Assert.True(Directory.Exists(fixture.Paths.DataDirectory));
        Assert.DoesNotContain("stop", fixture.Services.Operations);
        Assert.DoesNotContain("restore", fixture.Services.Operations);
    }

    [Theory]
    [InlineData("service-stop-pending")]
    [InlineData("rollback-completed")]
    public async Task DeployAsync_MissingPreexistingDataFailsBeforeServiceMutation(string stage)
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        Directory.Delete(fixture.Paths.DataDirectory, recursive: true);
        var journalStore = WritePendingJournal(
            fixture,
            stage,
            mutationStarted: true,
            installMovedToBackup: stage == "rollback-completed",
            stagingActivated: stage == "rollback-completed",
            formatVersion: DeploymentJournalStore.CurrentFormatVersion,
            dataDirectoryExistedBefore: true,
            dataDirectoryCreated: false);

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RecoveryRequired, result.Code);
        Assert.True(journalStore.Exists);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.False(Directory.Exists(fixture.Paths.DataDirectory));
        Assert.DoesNotContain("stop", fixture.Services.Operations);
        Assert.DoesNotContain("restore", fixture.Services.Operations);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DeployAsync_CommittedJournalMissingAuthoritativeStatePreservesRecoveryData(
        bool removeInstall,
        bool removeData)
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var journalStore = WritePendingJournal(
            fixture,
            "committed",
            mutationStarted: true,
            installMovedToBackup: true,
            stagingActivated: true,
            formatVersion: DeploymentJournalStore.CurrentFormatVersion,
            dataDirectoryExistedBefore: true,
            dataDirectoryCreated: false);
        var pending = journalStore.Read();
        if (removeInstall)
        {
            Directory.Move(fixture.Paths.InstallDirectory, pending.BackupDirectory);
        }
        if (removeData)
        {
            Directory.Delete(fixture.Paths.DataDirectory, recursive: true);
        }

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RecoveryRequired, result.Code);
        Assert.True(journalStore.Exists);
        Assert.Equal(removeInstall, Directory.Exists(pending.BackupDirectory));
        Assert.Equal(removeData, !Directory.Exists(fixture.Paths.DataDirectory));
        Assert.DoesNotContain("stop", fixture.Services.Operations);
        Assert.DoesNotContain("restore", fixture.Services.Operations);
    }

    [Fact]
    public async Task DeployAsync_MachineLockRejected_ReturnsStableErrorWithoutMutation()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateFreshFixture(folder);
        fixture.MachineLock.AcquireException = new SetupException(
            SetupErrorCodes.AlreadyRunning,
            "another setup is already running");
        var orchestrator = fixture.CreateOrchestrator(ready: true);

        var result = await orchestrator.DeployAsync(
            new SetupRequest("10.1.1.20", ["10.30.0.0/16"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.AlreadyRunning, result.Code);
        Assert.DoesNotContain("install", fixture.Services.Operations);
        Assert.DoesNotContain("apply", fixture.Firewall.Operations);
        Assert.False(Directory.Exists(fixture.Paths.InstallDirectory));
        Assert.Equal(1, fixture.MachineLock.AcquireCount);
        Assert.Equal(0, fixture.MachineLock.ReleaseCount);
    }

    [Fact]
    public async Task DeployAsync_AllowsOnlyStoppedLegacyLocalServiceMigration()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.Services = new FakeServiceManager(fixture.Services.State with
        {
            Running = false,
            AccountName = @"NT AUTHORITY\LocalService"
        });

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            @"NT SERVICE\SamsungSwitchWatchAgent",
            fixture.Services.State.AccountName);
    }

    [Fact]
    public async Task DeployAsync_RejectsRunningLegacyLocalServiceBeforeMutation()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.Services = new FakeServiceManager(fixture.Services.State with
        {
            Running = true,
            AccountName = @"NT AUTHORITY\LocalService"
        });

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.ServiceFailed, result.Code);
        Assert.DoesNotContain("stop", fixture.Services.Operations);
        Assert.DoesNotContain("install", fixture.Services.Operations);
    }

    private static DeploymentJournalStore WritePendingJournal(
        DeploymentFixture fixture,
        string stage,
        bool mutationStarted,
        bool installMovedToBackup,
        bool stagingActivated,
        int formatVersion = DeploymentJournalStore.LegacyFormatVersion,
        bool dataDirectoryExistedBefore = true,
        bool dataDirectoryCreated = false)
    {
        var transactionId = new string('c', 32);
        var store = new DeploymentJournalStore(fixture.FileSystem, fixture.Paths);
        store.Write(new DeploymentJournal(
            formatVersion,
            transactionId,
            stage,
            "0.10.1-poc",
            $"{fixture.Paths.InstallDirectory}.__staging_{transactionId}",
            $"{fixture.Paths.InstallDirectory}.__backup_{transactionId}",
            $"{fixture.Paths.InstallDirectory}.__failed_{transactionId}",
            mutationStarted,
            installMovedToBackup,
            stagingActivated,
            dataDirectoryExistedBefore,
            dataDirectoryCreated,
            fixture.Services.State,
            fixture.Firewall.State,
            FirewallRuleSnapshot.Missing(SetupConstants.LegacyFirewallRuleName)));
        return store;
    }

    private static DeploymentFixture CreateUpgradeFixture(TemporaryFolder folder)
    {
        var fixture = CreateFreshFixture(folder);
        Directory.CreateDirectory(fixture.Paths.InstallDirectory);
        Directory.CreateDirectory(fixture.Paths.DataDirectory);
        File.WriteAllText(fixture.Paths.AgentExecutablePath, "old-agent");
        File.WriteAllText(
            fixture.Paths.ProductionConfigurationPath,
            """
            {
              "Agent": {
                "AgentId": "preserved-agent",
                "RateLimitPerMinute": 45,
                "Telnet": {
                  "MaxSessionSeconds": 90,
                  "ImmediateSessionCloseRetryCount": 1,
                  "ImmediateSessionCloseRetryDelaySeconds": 3
                }
              }
            }
            """);
        File.WriteAllText(
            Path.Combine(fixture.Paths.DataDirectory, "https-certificate.pfx.dpapi"),
            "identity-secret");

        fixture.Services = new FakeServiceManager(new ServiceSnapshot(
            true,
            true,
            $"\"{fixture.Paths.AgentExecutablePath}\" --service",
            2,
            @"NT SERVICE\SamsungSwitchWatchAgent",
            "Legacy Agent Display",
            "Legacy Agent Description",
            0,
            new ServiceRecoverySnapshot(
                12345,
                false,
                "legacy reboot message",
                "legacy recovery command",
                [new ServiceFailureActionSnapshot(0, 4321)]),
            [9, 8, 7],
            3210));
        fixture.Firewall = new FakeFirewallManager(OwnedFirewall("192.168.1.10/32"));
        return fixture;
    }

    private static DeploymentFixture CreateFreshFixture(TemporaryFolder folder)
    {
        var package = folder.Combine("package");
        PackageFixture.Create(package);
        var paths = new DeploymentPaths(
            package,
            folder.Combine("program", "SamsungSwitchWatch", "Agent"),
            folder.Combine("data", "SamsungSwitchWatch"),
            folder.Combine("data", "SamsungSwitchWatch-Operations"));
        return new DeploymentFixture(
            paths,
            new TestFileSystem(),
            new FakeServiceManager(ServiceSnapshot.Missing),
            new FakeFirewallManager(
                FirewallRuleSnapshot.Missing(SetupConstants.FirewallRuleName)));
    }

    private static FirewallRuleSnapshot OwnedFirewall(string address) =>
        new(
            true,
            SetupConstants.FirewallRuleName,
            "Owned by SamsungSwitchWatchAgent installer v3",
            true,
            1,
            1,
            6,
            "18443",
            address,
            3,
            "All",
            false,
            string.Empty);

    private static FirewallSecurityAssessment OverlapAssessment() =>
        new(
        [
            new FirewallSecurityWarning(
                WindowsFirewallManager.FirewallOverlapWarningCode,
                "TCP/18443을 허용하는 다른 인바운드 방화벽 규칙 1개가 있습니다. " +
                "해당 규칙은 변경하지 않으며 Agent가 입력한 Viewer IP만 허용합니다.")
        ]);

    private sealed class DeploymentFixture(
        DeploymentPaths paths,
        TestFileSystem fileSystem,
        FakeServiceManager services,
        FakeFirewallManager firewall)
    {
        public DeploymentPaths Paths { get; } = paths;
        public TestFileSystem FileSystem { get; } = fileSystem;
        public FakeServiceManager Services { get; set; } = services;
        public FakeFirewallManager Firewall { get; set; } = firewall;
        public FakeMachineDeploymentLock MachineLock { get; } = new();

        public AgentDeploymentOrchestrator CreateOrchestrator(bool ready) =>
            new(
                new AgentPackageValidator(FileSystem),
                FileSystem,
                Services,
                Firewall,
                new FakeHealthProbe(ready),
                new FakeAdministratorChecker(),
                MachineLock,
                Paths);
    }
}
