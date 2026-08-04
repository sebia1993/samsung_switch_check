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
        Assert.True(
            fixture.Services.Operations.IndexOf("install") <
            fixture.Services.Operations.IndexOf("recovery-disabled"));
        Assert.True(
            fixture.Services.Operations.IndexOf("recovery-disabled") <
            fixture.Services.Operations.IndexOf("start"));
        Assert.True(
            fixture.Services.Operations.IndexOf("start") <
            fixture.Services.Operations.IndexOf("recovery"));
        var serviceStartedIndex = result.Steps
            .Select((step, index) => (step, index))
            .Single(item => item.step.Code == "SERVICE_STARTED")
            .index;
        var agentReadyIndex = result.Steps
            .Select((step, index) => (step, index))
            .Single(item => item.step.Code == "AGENT_READY")
            .index;
        Assert.True(serviceStartedIndex < agentReadyIndex);
    }

    [Fact]
    public async Task DeployAsync_ExistingPendingServiceStopsBeforeProgramSwap()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.Services = new FakeServiceManager(
            fixture.Services.State with
            {
                Running = false,
                ProcessId = 3210
            });

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.True(
            fixture.Services.Operations.IndexOf("stop") <
            fixture.Services.Operations.IndexOf("install"));
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
        Assert.Contains("recovery-disabled", fixture.Services.Operations);
        Assert.DoesNotContain("recovery", fixture.Services.Operations);
    }

    [Fact]
    public async Task DeployAsync_HealthFailureRecordsReadinessBeforeRollbackWithoutDuplicate()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var health = new FakeHealthProbe(
            ready: false,
            beforeResult: () => Thread.Sleep(TimeSpan.FromSeconds(1)));

        var result = await fixture.CreateOrchestrator(health).DeployAsync(
            new SetupRequest("192.168.1.20", ["10.20.0.0/16"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.HealthFailed, result.Code);
        var serviceStarted = Assert.Single(
            result.Steps,
            step => step.Code == "SERVICE_STARTED");
        Assert.Equal(SetupStepState.Succeeded, serviceStarted.State);
        var healthFailed = Assert.Single(
            result.Steps,
            step => step.Code == SetupErrorCodes.HealthFailed);
        Assert.Equal(SetupStepState.Failed, healthFailed.State);
        var rollbackCompleted = Assert.Single(
            result.Steps,
            step => step.Code == "ROLLBACK_COMPLETED");
        Assert.Equal(SetupStepState.Succeeded, rollbackCompleted.State);

        var codes = result.Steps.Select(step => step.Code).ToArray();
        Assert.True(
            Array.IndexOf(codes, "SERVICE_STARTED") <
            Array.IndexOf(codes, SetupErrorCodes.HealthFailed));
        Assert.True(
            Array.IndexOf(codes, SetupErrorCodes.HealthFailed) <
            Array.IndexOf(codes, "ROLLBACK_COMPLETED"));

        var diagnostics = Assert.IsType<SetupOperationDiagnosticMetadata>(
            result.DiagnosticMetadata);
        var healthDiagnostic = Assert.Single(
            diagnostics.Stages,
            stage => stage.Code == SetupErrorCodes.HealthFailed);
        var rollbackDiagnostic = Assert.Single(
            diagnostics.Stages,
            stage => stage.Code == "ROLLBACK_COMPLETED");
        Assert.True(healthDiagnostic.DurationMilliseconds >= 900);
        Assert.True(
            rollbackDiagnostic.DurationMilliseconds <
            healthDiagnostic.DurationMilliseconds);
    }

    [Fact]
    public async Task DeployAsync_HealthFailureStopsPendingServiceBeforeRollbackMove()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var health = new FakeHealthProbe(
            ready: false,
            beforeResult: () => fixture.Services.SetState(
                fixture.Services.State with
                {
                    Running = false,
                    ProcessId = 9876
                }));
        var orchestrator = fixture.CreateOrchestrator(health);

        var result = await orchestrator.DeployAsync(
            new SetupRequest("192.168.1.20", ["10.20.0.0/16"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        var startIndex = fixture.Services.Operations.IndexOf("start");
        var rollbackStopIndex = fixture.Services.Operations.FindIndex(
            startIndex + 1,
            operation => operation == "stop");
        Assert.True(rollbackStopIndex > startIndex);
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
    public async Task DeployAsync_FreshHealthFailureRetriesTransientProgramMoveLock()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateFreshFixture(folder);
        fixture.FileSystem.MoveFailuresRemaining = 1;
        fixture.FileSystem.MoveFailurePredicate = (_, destination) =>
            Path.GetFileName(destination)
                .Contains(".__failed_", StringComparison.Ordinal);

        var result = await fixture.CreateOrchestrator(ready: false).DeployAsync(
            new SetupRequest("10.1.1.20", ["10.30.0.0/16"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.HealthFailed, result.Code);
        Assert.Empty(result.RollbackFailureCodes);
        Assert.False(Directory.Exists(fixture.Paths.InstallDirectory));
        Assert.False(Directory.Exists(fixture.Paths.DataDirectory));
        Assert.False(fixture.Services.State.Exists);
        Assert.False(new DeploymentJournalStore(
            fixture.FileSystem,
            fixture.Paths).Exists);
    }

    [Fact]
    public async Task DeployAsync_UnexpectedServiceStartFailurePreservesSafeDiagnosticsAndRollsBack()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.Services.StartException =
            new IOException(@"sensitive C:\service-start detail");

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.Unexpected, result.Code);
        Assert.Equal(SetupErrorCodes.Unexpected, result.PrimaryFailureCode);
        Assert.Equal(
            SetupFailureStage.ServiceStart,
            result.DiagnosticMetadata?.Failure?.Stage);
        Assert.Equal(
            SetupFailureCategory.Io,
            result.DiagnosticMetadata?.Failure?.Category);
        Assert.True(
            result.DiagnosticMetadata?.Failure?.DurationMilliseconds >= 0);
        Assert.Equal(
            "old-agent",
            File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.Contains(
            result.Steps,
            step => step.Code == "ROLLBACK_COMPLETED");
        Assert.DoesNotContain(
            result.Steps,
            step => step.Message.Contains(
                "sensitive",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeployAsync_UnexpectedHealthProbeFailurePreservesReadinessStage()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var health = new FakeHealthProbe(
            ready: false,
            beforeResult: () => throw new TimeoutException(
                "sensitive readiness detail"));

        var result = await fixture.CreateOrchestrator(health).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.Unexpected, result.Code);
        Assert.Equal(
            SetupFailureStage.Readiness,
            result.DiagnosticMetadata?.Failure?.Stage);
        Assert.Equal(
            SetupFailureCategory.Timeout,
            result.DiagnosticMetadata?.Failure?.Category);
        Assert.Equal(
            "old-agent",
            File.ReadAllText(fixture.Paths.AgentExecutablePath));
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

        var orchestrator = fixture.CreateOrchestrator(ready: true);
        var blocked = await orchestrator.DeployAsync(
            new SetupRequest("10.1.1.20", ["10.30.0.0/16"]),
            CancellationToken.None);

        Assert.False(blocked.Succeeded);
        Assert.Equal(SetupErrorCodes.RecoveryRequired, blocked.Code);
        var recovery = await orchestrator.RecoverAsync(CancellationToken.None);
        Assert.True(recovery.Succeeded);

        var second = await orchestrator.DeployAsync(
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

        var orchestrator = fixture.CreateOrchestrator(ready: true);
        var recovery = await orchestrator.RecoverAsync(CancellationToken.None);
        Assert.True(recovery.Succeeded);

        var second = await orchestrator.DeployAsync(
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
        fixture.FileSystem.JournalDeleteFailuresRemaining = 6;
        var journalStore = new DeploymentJournalStore(
            fixture.FileSystem,
            fixture.Paths);

        var first = await fixture.CreateOrchestrator(ready: false).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(first.Succeeded);
        Assert.Equal(SetupErrorCodes.RollbackFailed, first.Code);
        Assert.Equal(SetupErrorCodes.HealthFailed, first.PrimaryFailureCode);
        Assert.Contains(
            SetupErrorCodes.RollbackEvidenceCleanupFailed,
            first.RollbackFailureCodes);
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

        var orchestrator = fixture.CreateOrchestrator(ready: true);
        var firstRecovery = await orchestrator.RecoverAsync(CancellationToken.None);
        Assert.False(firstRecovery.Succeeded);
        Assert.Equal(SetupErrorCodes.RollbackFailed, firstRecovery.Code);
        Assert.Contains(
            SetupErrorCodes.RollbackEvidenceCleanupFailed,
            firstRecovery.RollbackFailureCodes);
        Assert.True(journalStore.Exists);

        var secondRecovery = await orchestrator.RecoverAsync(CancellationToken.None);
        Assert.True(secondRecovery.Succeeded);
        Assert.False(journalStore.Exists);

        var third = await orchestrator.DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.True(third.Succeeded);
        Assert.Equal("new-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(fixture.Services.State.Running);
        Assert.False(journalStore.Exists);
    }

    [Fact]
    public async Task RecoverAsync_CommittedBackupCleanupRetriesAndNextDeploySucceeds()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var journalStore = WritePendingJournal(
            fixture,
            "committed",
            mutationStarted: true,
            installMovedToBackup: true,
            stagingActivated: true,
            formatVersion: DeploymentJournalStore.CurrentFormatVersion);
        var pending = journalStore.Read();
        Directory.CreateDirectory(pending.BackupDirectory);
        File.WriteAllText(
            Path.Combine(pending.BackupDirectory, "old-agent.txt"),
            "old-agent");
        fixture.FileSystem.BackupDirectoryCleanupFailuresRemaining = 2;

        var orchestrator = fixture.CreateOrchestrator(ready: true);
        var recovery = await orchestrator.RecoverAsync(CancellationToken.None);

        Assert.True(recovery.Succeeded);
        Assert.Equal(3, fixture.FileSystem.BackupDirectoryCleanupAttempts);
        Assert.Equal(
            2,
            fixture.FileSystem.AccessRequests.Count(request =>
                PhysicalSetupFileSystem.SamePath(
                    request.Path,
                    pending.BackupDirectory) &&
                request.Kind == DirectoryAccessKind.AdministratorOnly));
        Assert.False(Directory.Exists(pending.BackupDirectory));
        Assert.False(journalStore.Exists);

        var deployment = await orchestrator.DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.True(deployment.Succeeded);
        Assert.Equal("new-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.False(journalStore.Exists);
    }

    [Fact]
    public async Task RecoverAsync_DirectoryDeleteIOExceptionWithFalseProbeRetries()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var journalStore = WritePendingJournal(
            fixture,
            "committed",
            mutationStarted: true,
            installMovedToBackup: true,
            stagingActivated: true,
            formatVersion: DeploymentJournalStore.CurrentFormatVersion);
        var pending = journalStore.Read();
        Directory.CreateDirectory(pending.BackupDirectory);
        fixture.FileSystem.BackupDirectoryCleanupFailuresRemaining = 1;
        fixture.FileSystem
            .HideCleanupDirectoryAfterDeleteFailureUntilAccessNormalization = true;
        fixture.FileSystem.KeepFalseProbeAfterAccessNormalization = true;

        var result = await fixture.CreateOrchestrator(ready: true)
            .RecoverAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, fixture.FileSystem.BackupDirectoryCleanupAttempts);
        Assert.Single(
            fixture.FileSystem.AccessRequests,
            request =>
                PhysicalSetupFileSystem.SamePath(
                    request.Path,
                    pending.BackupDirectory) &&
                request.Kind == DirectoryAccessKind.AdministratorOnly);
        Assert.False(Directory.Exists(pending.BackupDirectory));
        Assert.False(journalStore.Exists);
    }

    [Theory]
    [InlineData("staging", SetupErrorCodes.RollbackStagingCleanupFailed)]
    [InlineData("backup", SetupErrorCodes.RollbackBackupCleanupFailed)]
    [InlineData("failed", SetupErrorCodes.RollbackFailedDirectoryCleanupFailed)]
    public async Task RecoverAsync_PersistentCommittedDirectoryCleanupFailsClosed(
        string target,
        string expectedCode)
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var journalStore = WritePendingJournal(
            fixture,
            "committed",
            mutationStarted: true,
            installMovedToBackup: true,
            stagingActivated: true,
            formatVersion: DeploymentJournalStore.CurrentFormatVersion);
        var pending = journalStore.Read();
        var targetPath = target switch
        {
            "staging" => pending.StagingDirectory,
            "backup" => pending.BackupDirectory,
            _ => pending.FailedDirectory
        };
        Directory.CreateDirectory(targetPath);
        switch (target)
        {
            case "staging":
                fixture.FileSystem.StagingDirectoryCleanupFailuresRemaining = 10;
                break;
            case "backup":
                fixture.FileSystem.BackupDirectoryCleanupFailuresRemaining = 10;
                break;
            default:
                fixture.FileSystem.FailedDirectoryCleanupFailuresRemaining = 10;
                break;
        }

        var result = await fixture.CreateOrchestrator(ready: true)
            .RecoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RollbackFailed, result.Code);
        Assert.Contains(expectedCode, result.RollbackFailureCodes);
        Assert.Contains(
            SetupErrorCodes.RollbackEvidenceCleanupFailed,
            result.RollbackFailureCodes);
        Assert.True(journalStore.Exists);
        Assert.True(Directory.Exists(targetPath));
        Assert.Equal(
            2,
            fixture.FileSystem.AccessRequests.Count(request =>
                PhysicalSetupFileSystem.SamePath(request.Path, targetPath) &&
                request.Kind == DirectoryAccessKind.AdministratorOnly));
        Assert.Equal(
            3,
            target switch
            {
                "staging" => fixture.FileSystem.StagingDirectoryCleanupAttempts,
                "backup" => fixture.FileSystem.BackupDirectoryCleanupAttempts,
                _ => fixture.FileSystem.FailedDirectoryCleanupAttempts
            });
    }

    [Fact]
    public async Task RecoverAsync_SilentJournalDeleteNeverReturnsSuccess()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var journalStore = WritePendingJournal(
            fixture,
            "committed",
            mutationStarted: true,
            installMovedToBackup: true,
            stagingActivated: true,
            formatVersion: DeploymentJournalStore.CurrentFormatVersion);
        fixture.FileSystem.SilentJournalDeleteAttemptsRemaining = 3;
        var operationsAccessBefore = fixture.FileSystem.AccessRequests.Count(
            request =>
                PhysicalSetupFileSystem.SamePath(
                    request.Path,
                    fixture.Paths.OperationsDirectory) &&
                request.Kind == DirectoryAccessKind.AdministratorOnly);

        var result = await fixture.CreateOrchestrator(ready: true)
            .RecoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RollbackFailed, result.Code);
        Assert.Contains(
            SetupErrorCodes.RollbackJournalCleanupFailed,
            result.RollbackFailureCodes);
        Assert.Contains(
            SetupErrorCodes.RollbackEvidenceCleanupFailed,
            result.RollbackFailureCodes);
        Assert.Equal(3, fixture.FileSystem.JournalDeleteAttempts);
        Assert.True(journalStore.Exists);
        Assert.Equal(
            operationsAccessBefore + 1,
            fixture.FileSystem.AccessRequests.Count(request =>
                PhysicalSetupFileSystem.SamePath(
                    request.Path,
                    fixture.Paths.OperationsDirectory) &&
                request.Kind == DirectoryAccessKind.AdministratorOnly));
    }

    [Fact]
    public async Task RecoverAsync_DeleteIOExceptionWithFalseProbeStillRetriesDelete()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var journalStore = WritePendingJournal(
            fixture,
            "committed",
            mutationStarted: true,
            installMovedToBackup: true,
            stagingActivated: true,
            formatVersion: DeploymentJournalStore.CurrentFormatVersion);
        fixture.FileSystem.JournalDeleteFailuresRemaining = 1;
        fixture.FileSystem.HideJournalAfterDeleteFailureUntilAccessNormalization = true;
        fixture.FileSystem.KeepFalseProbeAfterAccessNormalization = true;
        var operationsAccessBefore = fixture.FileSystem.AccessRequests.Count(
            request =>
                PhysicalSetupFileSystem.SamePath(
                    request.Path,
                    fixture.Paths.OperationsDirectory) &&
                request.Kind == DirectoryAccessKind.AdministratorOnly);

        var result = await fixture.CreateOrchestrator(ready: true)
            .RecoverAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(2, fixture.FileSystem.JournalDeleteAttempts);
        Assert.Equal(
            operationsAccessBefore + 1,
            fixture.FileSystem.AccessRequests.Count(request =>
                PhysicalSetupFileSystem.SamePath(
                    request.Path,
                    fixture.Paths.OperationsDirectory) &&
                request.Kind == DirectoryAccessKind.AdministratorOnly));
        Assert.False(File.Exists(journalStore.JournalPath));
        Assert.False(journalStore.Exists);
    }

    [Fact]
    public async Task RecoverAsync_JournalReappearanceFailsFinalPostcondition()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var journalStore = WritePendingJournal(
            fixture,
            "committed",
            mutationStarted: true,
            installMovedToBackup: true,
            stagingActivated: true,
            formatVersion: DeploymentJournalStore.CurrentFormatVersion);
        fixture.FileSystem.RecreateJournalAfterDeleteVerification = true;

        var result = await fixture.CreateOrchestrator(ready: true)
            .RecoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RollbackFailed, result.Code);
        Assert.Contains(
            SetupErrorCodes.RollbackJournalCleanupFailed,
            result.RollbackFailureCodes);
        Assert.Contains(
            SetupErrorCodes.RollbackEvidenceCleanupFailed,
            result.RollbackFailureCodes);
        Assert.Equal(1, fixture.FileSystem.JournalDeleteAttempts);
        Assert.True(journalStore.Exists);
    }

    [Fact]
    public async Task RecoverAsync_CancellationDuringCleanupDelayPreservesJournal()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var journalStore = WritePendingJournal(
            fixture,
            "committed",
            mutationStarted: true,
            installMovedToBackup: true,
            stagingActivated: true,
            formatVersion: DeploymentJournalStore.CurrentFormatVersion);
        var pending = journalStore.Read();
        Directory.CreateDirectory(pending.BackupDirectory);
        fixture.FileSystem.BackupDirectoryCleanupFailuresRemaining = 10;
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(50));

        var result = await fixture.CreateOrchestrator(ready: true)
            .RecoverAsync(cancellation.Token);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.Cancelled, result.Code);
        Assert.True(journalStore.Exists);
        Assert.True(Directory.Exists(pending.BackupDirectory));
        Assert.Equal(1, fixture.FileSystem.BackupDirectoryCleanupAttempts);
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
        Assert.Contains(
            SetupErrorCodes.RollbackJournalWriteFailed,
            first.RollbackFailureCodes);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(journalStore.Exists);
        Assert.NotEqual("rollback-completed", journalStore.Read().Stage);
        Assert.Single(Directory.GetDirectories(
            Path.GetDirectoryName(fixture.Paths.InstallDirectory)!,
            $"{Path.GetFileName(fixture.Paths.InstallDirectory)}.__failed_*"));

        var orchestrator = fixture.CreateOrchestrator(ready: true);
        var recovery = await orchestrator.RecoverAsync(CancellationToken.None);
        Assert.True(recovery.Succeeded);

        var second = await orchestrator.DeployAsync(
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
        Assert.Contains(
            SetupErrorCodes.RollbackJournalWriteFailed,
            first.RollbackFailureCodes);
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

        var orchestrator = fixture.CreateOrchestrator(ready: true);
        var recovery = await orchestrator.RecoverAsync(CancellationToken.None);
        Assert.True(recovery.Succeeded);

        var second = await orchestrator.DeployAsync(
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
        fixture.FileSystem.FailedDirectoryCleanupFailuresRemaining = 3;
        var journalStore = new DeploymentJournalStore(
            fixture.FileSystem,
            fixture.Paths);

        var first = await fixture.CreateOrchestrator(ready: false).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(first.Succeeded);
        Assert.Equal(SetupErrorCodes.RollbackFailed, first.Code);
        Assert.Contains(
            SetupErrorCodes.RollbackEvidenceCleanupFailed,
            first.RollbackFailureCodes);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(journalStore.Exists);
        Assert.Equal("rollback-completed", journalStore.Read().Stage);
        Assert.Single(Directory.GetDirectories(
            Path.GetDirectoryName(fixture.Paths.InstallDirectory)!,
            $"{Path.GetFileName(fixture.Paths.InstallDirectory)}.__failed_*"));

        var orchestrator = fixture.CreateOrchestrator(ready: true);
        var recovery = await orchestrator.RecoverAsync(CancellationToken.None);
        Assert.True(recovery.Succeeded);

        var second = await orchestrator.DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.True(second.Succeeded);
        Assert.Equal("new-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(fixture.Services.State.Running);
        Assert.False(journalStore.Exists);
    }

    [Fact]
    public async Task DeployAsync_RollbackServiceStopFailureBlocksFileAndServiceRestore()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.Services.StopFailuresRemaining = 1;
        fixture.Services.StopFailureOccurrence = 2;

        var result = await fixture.CreateOrchestrator(ready: false).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RollbackFailed, result.Code);
        Assert.Equal(SetupErrorCodes.HealthFailed, result.PrimaryFailureCode);
        Assert.Equal(
            SetupErrorCodes.RollbackServiceStopFailed,
            Assert.Single(result.RollbackFailureCodes));
        Assert.Contains(
            result.Steps,
            step => step.Code == SetupErrorCodes.RollbackServiceStopFailed);
        Assert.Single(
            result.Steps,
            step => step.Code == SetupErrorCodes.HealthFailed);
        Assert.Equal(SetupErrorCodes.RollbackFailed, result.Steps.Last().Code);
        Assert.Single(
            result.Steps,
            step => step.Code == SetupErrorCodes.RollbackFailed);
        Assert.DoesNotContain("restore", fixture.Services.Operations);
        Assert.Equal(
            "new-agent",
            File.ReadAllText(fixture.Paths.AgentExecutablePath));
    }

    [Fact]
    public async Task DeployAsync_TransientRollbackFileMoveFailureRestoresPreviousService()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.FileSystem.MoveFailuresRemaining = 1;
        fixture.FileSystem.MoveFailurePredicate = (_, destination) =>
            Path.GetFileName(destination)
                .Contains(".__failed_", StringComparison.Ordinal);

        var result = await fixture.CreateOrchestrator(ready: false).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.HealthFailed, result.Code);
        Assert.Equal(SetupErrorCodes.HealthFailed, result.PrimaryFailureCode);
        Assert.DoesNotContain(
            SetupErrorCodes.RollbackFileRestoreFailed,
            result.RollbackFailureCodes);
        Assert.Contains("restore", fixture.Services.Operations);
        Assert.True(fixture.Services.State.Running);
        Assert.Equal(
            "old-agent",
            File.ReadAllText(fixture.Paths.AgentExecutablePath));
    }

    [Fact]
    public async Task DeployAsync_PersistentRollbackFileMoveFailureNeverRestartsPreviousService()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var health = new FakeHealthProbe(
            ready: false,
            failureCode: AgentHealthProbeCode.HttpsConnectionReset,
            serviceRunningObserved: true,
            listenerOwnedObserved: true,
            httpAttemptCount: 3,
            lastTransportPhase: AgentHealthTransportPhase.RequestStarted);
        fixture.FileSystem.MoveFailuresRemaining = 10;
        fixture.FileSystem.MoveFailurePredicate = (_, destination) =>
            Path.GetFileName(destination)
                .Contains(".__failed_", StringComparison.Ordinal);

        var result = await fixture.CreateOrchestrator(health).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RollbackFailed, result.Code);
        Assert.Equal(SetupErrorCodes.HealthFailed, result.PrimaryFailureCode);
        Assert.Equal(
            AgentHealthProbeCode.HttpsConnectionReset.ToString(),
            result.AgentHealthCode);
        Assert.True(result.AgentServiceRunningObserved);
        Assert.True(result.AgentListenerOwnedObserved);
        Assert.Equal(3, result.AgentHttpAttemptCount);
        Assert.Equal(
            AgentHealthTransportPhase.RequestStarted,
            result.AgentLastTransportPhase);
        Assert.Equal(
            "Agent PC 내부 통신 실패: Setup → 127.0.0.1:18443 → Agent 서비스 구간에서 " +
            "로컬 HTTPS 응답을 확인하지 못했습니다. Viewer IP나 스위치 관리망 설정 문제는 아닙니다. " +
            "진단 단계: 로컬 HTTPS 연결 재설정 / HTTPS 진행: HTTPS 요청 시작.",
            result.PrimaryFailureMessage);
        Assert.Contains(
            SetupErrorCodes.RollbackFileRestoreFailed,
            result.RollbackFailureCodes);
        Assert.DoesNotContain("restore", fixture.Services.Operations);
        Assert.False(fixture.Services.State.Running);
        var pending = new DeploymentJournalStore(
            fixture.FileSystem,
            fixture.Paths).Read();
        Assert.Equal(
            AgentHealthProbeCode.HttpsConnectionReset.ToString(),
            pending.AgentHealthCode);
        Assert.False(pending.AgentRestartObserved);
        Assert.True(pending.AgentServiceRunningObserved);
        Assert.True(pending.AgentListenerOwnedObserved);
        Assert.Equal(3, pending.AgentHttpAttemptCount);
        Assert.Equal(
            AgentHealthTransportPhase.RequestStarted,
            pending.AgentLastTransportPhase);
        var inspection = fixture.CreateOrchestrator(ready: true)
            .InspectPendingRecovery();
        Assert.True(inspection.AgentServiceRunningObserved);
        Assert.True(inspection.AgentListenerOwnedObserved);
        Assert.Equal(3, inspection.AgentHttpAttemptCount);
        Assert.Equal(
            AgentHealthTransportPhase.RequestStarted,
            inspection.AgentLastTransportPhase);
    }

    [Fact]
    public async Task DeployAsync_RollbackServiceRestoreFailureKeepsOriginalFailure()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.Services.RestoreFailuresRemaining = 1;

        var result = await fixture.CreateOrchestrator(ready: false).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RollbackFailed, result.Code);
        Assert.Equal(SetupErrorCodes.HealthFailed, result.PrimaryFailureCode);
        Assert.NotNull(result.PrimaryFailureMessage);
        Assert.Contains(
            SetupErrorCodes.RollbackServiceRestoreFailed,
            result.RollbackFailureCodes);
        Assert.Equal(
            SetupErrorCodes.HealthFailed,
            new DeploymentJournalStore(fixture.FileSystem, fixture.Paths)
                .Read()
                .PrimaryFailureCode);
    }

    [Fact]
    public async Task DeployAsync_FirewallRollbackAttemptsHttpsAndLegacyIndependently()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.Firewall.RestoreFailureRuleNames.Add(
            SetupConstants.FirewallRuleName);

        var result = await fixture.CreateOrchestrator(ready: false).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RollbackFailed, result.Code);
        Assert.Contains(
            SetupErrorCodes.RollbackHttpsFirewallRestoreFailed,
            result.RollbackFailureCodes);
        Assert.DoesNotContain(
            SetupErrorCodes.RollbackLegacyFirewallRestoreFailed,
            result.RollbackFailureCodes);
        Assert.Contains(
            $"restore:{SetupConstants.FirewallRuleName}",
            fixture.Firewall.Operations);
        Assert.Contains(
            $"restore:{SetupConstants.LegacyFirewallRuleName}",
            fixture.Firewall.Operations);
    }

    [Fact]
    public async Task DeployAsync_FirewallRollbackReportsBothIndependentFailures()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.Firewall.RestoreFailureRuleNames.Add(
            SetupConstants.FirewallRuleName);
        fixture.Firewall.RestoreFailureRuleNames.Add(
            SetupConstants.LegacyFirewallRuleName);

        var result = await fixture.CreateOrchestrator(ready: false).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.Contains(
            SetupErrorCodes.RollbackHttpsFirewallRestoreFailed,
            result.RollbackFailureCodes);
        Assert.Contains(
            SetupErrorCodes.RollbackLegacyFirewallRestoreFailed,
            result.RollbackFailureCodes);
        Assert.Contains(
            $"restore:{SetupConstants.FirewallRuleName}",
            fixture.Firewall.Operations);
        Assert.Contains(
            $"restore:{SetupConstants.LegacyFirewallRuleName}",
            fixture.Firewall.Operations);
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
        Assert.Contains(
            result.Steps,
            step => step.Code == SetupErrorCodes.RollbackBackupCleanupFailed);
        var journalStore = new DeploymentJournalStore(
            fixture.FileSystem,
            fixture.Paths);
        Assert.True(journalStore.Exists);
        var pending = journalStore.Read();
        Assert.Equal("committed", pending.Stage);
        Assert.True(Directory.Exists(pending.BackupDirectory));
    }

    [Fact]
    public async Task DeployAsync_CommittedJournalCleanupRetriesBeforeSuccess()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.FileSystem.JournalDeleteFailuresRemaining = 2;

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("192.168.1.20", ["192.168.50.0/24"]),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(3, fixture.FileSystem.JournalDeleteAttempts);
        Assert.DoesNotContain(
            result.Steps,
            step => step.Code == "JOURNAL_CLEANUP_PENDING");
        Assert.False(new DeploymentJournalStore(
            fixture.FileSystem,
            fixture.Paths).Exists);
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
    public async Task Diagnostics_UnexpectedHealthFailurePreservesSafeStageAndCategory()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var diagnostics = new SetupDiagnosticsService(
            new AgentPackageValidator(fixture.FileSystem),
            fixture.FileSystem,
            fixture.Services,
            fixture.Firewall,
            new FakeHealthProbe(
                ready: false,
                beforeResult: () => throw new InvalidOperationException(
                    "sensitive health detail")),
            new FakeAdministratorChecker(),
            fixture.Paths);

        var result = await diagnostics.RunAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.Unexpected, result.Code);
        Assert.Equal(
            SetupFailureStage.Readiness,
            result.DiagnosticMetadata?.Failure?.Stage);
        Assert.Equal(
            SetupFailureCategory.InvalidState,
            result.DiagnosticMetadata?.Failure?.Category);
        Assert.DoesNotContain(
            result.Steps,
            step => step.Message.Contains(
                "sensitive",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Diagnostics_NotReadyAgentRemainsVisibleWithoutBlockingInstallPreflight()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var diagnostics = new SetupDiagnosticsService(
            new AgentPackageValidator(fixture.FileSystem),
            fixture.FileSystem,
            fixture.Services,
            fixture.Firewall,
            new FakeHealthProbe(
                ready: false,
                failureCode: AgentHealthProbeCode.HttpsRequestTimeout,
                serviceRunningObserved: true,
                listenerOwnedObserved: true,
                httpAttemptCount: 2,
                lastTransportPhase: AgentHealthTransportPhase.RequestStarted),
            new FakeAdministratorChecker(),
            fixture.Paths);

        var result = await diagnostics.RunAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            AgentHealthProbeCode.HttpsRequestTimeout.ToString(),
            result.AgentHealthCode);
        Assert.True(result.AgentServiceRunningObserved);
        Assert.True(result.AgentListenerOwnedObserved);
        Assert.Equal(2, result.AgentHttpAttemptCount);
        Assert.Equal(
            AgentHealthTransportPhase.RequestStarted,
            result.AgentLastTransportPhase);
        var readiness = Assert.Single(
            result.Steps,
            step => step.Code == "AGENT_NOT_READY");
        Assert.Equal(SetupStepState.Information, readiness.State);
        Assert.Contains(
            AgentDeploymentOrchestrator.AgentHealthDisplayName(
                AgentHealthProbeCode.HttpsRequestTimeout),
            readiness.Message,
            StringComparison.Ordinal);
        Assert.StartsWith(
            "Agent PC 내부 통신 실패: Setup → 127.0.0.1:18443 → Agent 서비스 구간에서 " +
            "로컬 HTTPS 응답을 확인하지 못했습니다. Viewer IP나 스위치 관리망 설정 문제는 아닙니다.",
            readiness.Message,
            StringComparison.Ordinal);
        Assert.Contains(
            "HTTPS 진행: HTTPS 요청 시작",
            readiness.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "192.168.1.20",
            readiness.Message,
            StringComparison.Ordinal);
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
    public async Task Diagnostics_AcceptsWindowsDottedMaskReadbackAsExactViewerRule()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateFreshFixture(folder);
        fixture.Firewall = new FakeFirewallManager(
            OwnedFirewall("192.168.1.20/255.255.255.255"));
        var diagnostics = new SetupDiagnosticsService(
            new AgentPackageValidator(fixture.FileSystem),
            fixture.FileSystem,
            fixture.Services,
            fixture.Firewall,
            new FakeHealthProbe(true),
            new FakeAdministratorChecker(),
            fixture.Paths);

        var result = await diagnostics.RunAsync(
            new SetupRequest("192.168.1.20", ["192.168.40.0/24"]),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Contains(
            result.Steps,
            step =>
                step.Code == "FIREWALL_EXACT" &&
                step.State == SetupStepState.Succeeded);
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
    public async Task DeployAsync_RetriesDelayedFirewallVisibilityAndAcceptsDottedMask()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateFreshFixture(folder);
        fixture.Firewall.AppliedRuleReadback = capture =>
            capture < 3
                ? OwnedFirewall("10.0.0.0/8")
                : OwnedFirewall("10.1.1.20/255.255.255.255");

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("10.1.1.20", ["10.30.0.0/16"]),
            CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(3, fixture.Firewall.AppliedRuleCaptureCount);
        Assert.True(fixture.Services.State.Running);
    }

    [Fact]
    public async Task DeployAsync_FirewallVerificationTimeoutRollsBackWithSanitizedMismatch()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateFreshFixture(folder);
        fixture.Firewall.AppliedRuleReadback = _ => OwnedFirewall("Any");

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("10.1.1.20", ["10.30.0.0/16"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.FirewallFailed, result.Code);
        Assert.Contains(
            FirewallRuleMismatchCodes.RemoteAddress,
            result.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain("10.1.1.20", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Any", result.Message, StringComparison.Ordinal);
        Assert.Equal(11, fixture.Firewall.AppliedRuleCaptureCount);
        Assert.Contains(
            $"restore:{SetupConstants.FirewallRuleName}",
            fixture.Firewall.Operations);
        Assert.False(fixture.Firewall.State.Exists);
        Assert.False(Directory.Exists(fixture.Paths.InstallDirectory));
    }

    [Fact]
    public async Task DeployAsync_CancellationDuringFirewallRetryRollsBack()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateFreshFixture(folder);
        fixture.Firewall.AppliedRuleReadback = _ => OwnedFirewall("Any");
        using var cancellation = new CancellationTokenSource();

        var deployment = fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("10.1.1.20", ["10.30.0.0/16"]),
            cancellation.Token);
        Assert.True(
            fixture.Firewall.AppliedRuleCaptured.Wait(TimeSpan.FromSeconds(5)),
            "Firewall verification retry did not start before the test deadline.");
        cancellation.Cancel();

        var result = await deployment.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.Cancelled, result.Code);
        Assert.Contains(
            $"restore:{SetupConstants.FirewallRuleName}",
            fixture.Firewall.Operations);
        Assert.False(fixture.Firewall.State.Exists);
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
    public async Task DeployAsync_RefusesPendingBackupUntilExplicitRecovery()
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
        var journalStore = new DeploymentJournalStore(
            fixture.FileSystem,
            fixture.Paths);
        journalStore.Write(
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

        var blocked = await orchestrator.DeployAsync(
            new SetupRequest(string.Empty, []),
            CancellationToken.None);

        Assert.False(blocked.Succeeded);
        Assert.Equal(SetupErrorCodes.RecoveryRequired, blocked.Code);
        Assert.False(Directory.Exists(fixture.Paths.InstallDirectory));
        Assert.True(journalStore.Exists);
        Assert.DoesNotContain("restore", fixture.Services.Operations);

        var recovery = await orchestrator.RecoverAsync(CancellationToken.None);

        Assert.True(recovery.Succeeded);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(fixture.Services.State.Running);
        Assert.False(journalStore.Exists);
        Assert.Contains(
            recovery.Steps,
            step => step.Code == "ROLLBACK_COMPLETED");

        var invalidInput = await orchestrator.DeployAsync(
            new SetupRequest(string.Empty, []),
            CancellationToken.None);
        Assert.Equal(SetupErrorCodes.ViewerIpInvalid, invalidInput.Code);
    }

    [Fact]
    public void InspectPendingRecovery_IsReadOnlyAndReportsSafeEvidence()
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
        Directory.CreateDirectory(pending.StagingDirectory);
        var operationsBefore = fixture.Services.Operations.ToArray();

        var inspection = fixture.CreateOrchestrator(ready: true)
            .InspectPendingRecovery();

        Assert.True(inspection.Exists);
        Assert.True(inspection.CanRecover);
        Assert.Equal("service-stop-pending", inspection.JournalStage);
        Assert.Equal("running", inspection.ServiceState);
        Assert.True(inspection.InstallDirectoryExists);
        Assert.True(inspection.StagingDirectoryExists);
        Assert.False(inspection.BackupDirectoryExists);
        Assert.False(inspection.FailedDirectoryExists);
        Assert.True(inspection.DataDirectoryExists);
        Assert.Equal(
            operationsBefore.Append("capture"),
            fixture.Services.Operations);
        Assert.True(journalStore.Exists);
    }

    [Fact]
    public async Task RecoverAsync_ServiceStopPendingUpgradeKeepsInstallAndRestoresService()
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
        Directory.CreateDirectory(pending.StagingDirectory);

        var result = await fixture.CreateOrchestrator(ready: true)
            .RecoverAsync(CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(
            "old-agent",
            File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.True(fixture.Services.State.Running);
        Assert.Contains("stop", fixture.Services.Operations);
        Assert.Contains("restore", fixture.Services.Operations);
        Assert.False(journalStore.Exists);
        Assert.False(Directory.Exists(pending.StagingDirectory));
        Assert.DoesNotContain(
            result.Steps,
            step => step.Code == SetupErrorCodes.RollbackFileRestoreFailed);
    }

    [Fact]
    public void InspectPendingRecovery_UnsafeJournalDisablesRecoveryWithoutMutation()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var journalStore = WritePendingJournal(
            fixture,
            "unknown-stage",
            mutationStarted: true,
            installMovedToBackup: false,
            stagingActivated: false,
            formatVersion: DeploymentJournalStore.CurrentFormatVersion);

        var inspection = fixture.CreateOrchestrator(ready: true)
            .InspectPendingRecovery();

        Assert.True(inspection.Exists);
        Assert.False(inspection.CanRecover);
        Assert.Equal(SetupErrorCodes.RecoveryRequired, inspection.Code);
        Assert.True(journalStore.Exists);
        Assert.Equal(
            "old-agent",
            File.ReadAllText(fixture.Paths.AgentExecutablePath));
        Assert.DoesNotContain("stop", fixture.Services.Operations);
        Assert.DoesNotContain("restore", fixture.Services.Operations);
    }

    [Fact]
    public void InspectPendingRecovery_InvalidServiceSnapshotDisablesRecovery()
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
        journalStore.Write(pending with
        {
            PreviousService = pending.PreviousService with
            {
                BinaryPath = @"""C:\Windows\System32\other.exe"""
            }
        });

        var inspection = fixture.CreateOrchestrator(ready: true)
            .InspectPendingRecovery();

        Assert.True(inspection.Exists);
        Assert.False(inspection.CanRecover);
        Assert.Equal(SetupErrorCodes.RecoveryRequired, inspection.Code);
        Assert.False(inspection.EvidenceStateKnown);
        Assert.DoesNotContain("stop", fixture.Services.Operations);
        Assert.DoesNotContain("restore", fixture.Services.Operations);
    }

    [Fact]
    public void InspectPendingRecovery_InvalidFirewallSnapshotDisablesRecovery()
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
        journalStore.Write(pending with
        {
            PreviousHttpsFirewall = pending.PreviousHttpsFirewall with
            {
                Name = "UnrelatedRule"
            }
        });

        var inspection = fixture.CreateOrchestrator(ready: true)
            .InspectPendingRecovery();

        Assert.True(inspection.Exists);
        Assert.False(inspection.CanRecover);
        Assert.Equal(SetupErrorCodes.RecoveryRequired, inspection.Code);
        Assert.DoesNotContain(
            fixture.Firewall.Operations,
            operation => operation.StartsWith(
                "restore:",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecoverAsync_UnexpectedWindowsFailureReturnsStableResult()
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
        journalStore.Write(journalStore.Read() with
        {
            AgentHealthCode = AgentHealthProbeCode.HttpsTlsFailed.ToString(),
            AgentServiceRunningObserved = true,
            AgentListenerOwnedObserved = true,
            AgentHttpAttemptCount = 1,
            AgentLastTransportPhase = AgentHealthTransportPhase.RequestStarted
        });
        fixture.Services.CaptureException =
            new InvalidOperationException("simulated Windows failure");

        var result = await fixture.CreateOrchestrator(ready: true)
            .RecoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.Unexpected, result.Code);
        Assert.Contains(
            result.Steps,
            step => step.Code == SetupErrorCodes.Unexpected);
        Assert.Equal(
            SetupFailureStage.Recovery,
            result.DiagnosticMetadata?.Failure?.Stage);
        Assert.Equal(
            SetupFailureCategory.InvalidState,
            result.DiagnosticMetadata?.Failure?.Category);
        Assert.True(
            result.DiagnosticMetadata?.Failure?.DurationMilliseconds >= 0);
        Assert.Equal(
            AgentHealthProbeCode.HttpsTlsFailed.ToString(),
            result.AgentHealthCode);
        Assert.True(result.AgentServiceRunningObserved);
        Assert.True(result.AgentListenerOwnedObserved);
        Assert.Equal(1, result.AgentHttpAttemptCount);
        Assert.Equal(
            AgentHealthTransportPhase.RequestStarted,
            result.AgentLastTransportPhase);
        Assert.True(journalStore.Exists);
    }

    [Theory]
    [InlineData(DeploymentJournalStore.LegacyFormatVersion)]
    [InlineData(DeploymentJournalStore.CurrentFormatVersion)]
    public void DeploymentJournal_ReadsOlderFormatWithoutOptionalFailureMetadata(
        int formatVersion)
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        var journalStore = WritePendingJournal(
            fixture,
            "service-stop-pending",
            mutationStarted: true,
            installMovedToBackup: false,
            stagingActivated: false,
            formatVersion: formatVersion);
        var json = JsonNode.Parse(File.ReadAllText(journalStore.JournalPath))!
            .AsObject();
        json.Remove(nameof(DeploymentJournal.PrimaryFailureCode));
        json.Remove(nameof(DeploymentJournal.PrimaryFailureMessage));
        json.Remove(nameof(DeploymentJournal.RollbackFailureCodes));
        json.Remove(nameof(DeploymentJournal.AgentHealthCode));
        json.Remove(nameof(DeploymentJournal.AgentRestartObserved));
        json.Remove(nameof(DeploymentJournal.AgentServiceRunningObserved));
        json.Remove(nameof(DeploymentJournal.AgentListenerOwnedObserved));
        json.Remove(nameof(DeploymentJournal.AgentHttpAttemptCount));
        json.Remove(nameof(DeploymentJournal.AgentLastTransportPhase));
        File.WriteAllText(journalStore.JournalPath, json.ToJsonString());

        var restored = journalStore.Read();

        Assert.Equal(formatVersion, restored.FormatVersion);
        Assert.Null(restored.PrimaryFailureCode);
        Assert.Null(restored.PrimaryFailureMessage);
        Assert.Empty(restored.RollbackFailureCodes);
        Assert.Null(restored.AgentHealthCode);
        Assert.False(restored.AgentRestartObserved);
        Assert.False(restored.AgentServiceRunningObserved);
        Assert.False(restored.AgentListenerOwnedObserved);
        Assert.Equal(0, restored.AgentHttpAttemptCount);
        Assert.Equal(
            AgentHealthTransportPhase.NotStarted,
            restored.AgentLastTransportPhase);
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
    public async Task RecoverAsync_LegacyPendingRecoveryUpgradesJournalBeforeRollback()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateUpgradeFixture(folder);
        fixture.FileSystem.JournalDeleteFailuresRemaining = 3;
        var journalStore = WritePendingJournal(
            fixture,
            "service-stop-pending",
            mutationStarted: true,
            installMovedToBackup: false,
            stagingActivated: false);

        var result = await fixture.CreateOrchestrator(ready: true)
            .RecoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RollbackFailed, result.Code);
        Assert.True(journalStore.Exists);
        Assert.Equal(
            DeploymentJournalStore.CurrentFormatVersion,
            journalStore.Read().FormatVersion);
        Assert.Equal("old-agent", File.ReadAllText(fixture.Paths.AgentExecutablePath));
    }

    [Fact]
    public async Task RecoverAsync_LegacyJournalUpgradeFailurePreservesStateBeforeMutation()
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

        var result = await fixture.CreateOrchestrator(ready: true)
            .RecoverAsync(CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.RollbackFailed, result.Code);
        Assert.Contains(
            SetupErrorCodes.RollbackJournalWriteFailed,
            result.RollbackFailureCodes);
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
    public async Task DeployAsync_UnexpectedMachineLockFailureReturnsStableDiagnosticResult()
    {
        using var folder = new TemporaryFolder();
        var fixture = CreateFreshFixture(folder);
        fixture.MachineLock.AcquireException =
            new UnauthorizedAccessException("sensitive lock detail");

        var result = await fixture.CreateOrchestrator(ready: true).DeployAsync(
            new SetupRequest("10.1.1.20", ["10.30.0.0/16"]),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(SetupErrorCodes.Unexpected, result.Code);
        Assert.Equal(
            SetupFailureStage.OperationLock,
            result.DiagnosticMetadata?.Failure?.Stage);
        Assert.Equal(
            SetupFailureCategory.AccessDenied,
            result.DiagnosticMetadata?.Failure?.Category);
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
            CreateOrchestrator(new FakeHealthProbe(ready));

        public AgentDeploymentOrchestrator CreateOrchestrator(
            IAgentHealthProbe healthProbe) =>
            new(
                new AgentPackageValidator(FileSystem),
                FileSystem,
                Services,
                Firewall,
                healthProbe,
                new FakeAdministratorChecker(),
                MachineLock,
                Paths);
    }
}
