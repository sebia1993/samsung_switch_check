namespace SamsungSwitchWatch.Agent.Setup.Deployment;

public sealed class AgentDeploymentOrchestrator(
    IAgentPackageValidator packageValidator,
    ISetupFileSystem fileSystem,
    IServiceManager serviceManager,
    IFirewallManager firewallManager,
    IAgentHealthProbe healthProbe,
    IAdministratorChecker administratorChecker,
    IMachineDeploymentLock machineDeploymentLock,
    DeploymentPaths paths)
{
    private static readonly SemaphoreSlim ProcessDeploymentGate = new(1, 1);
    private const string RollbackCompletedStage = "rollback-completed";
    private const int EvidenceCleanupMaxAttempts = 3;
    private static readonly TimeSpan EvidenceCleanupRetryDelay =
        TimeSpan.FromMilliseconds(250);
    private const int RollbackMoveMaxAttempts = 5;
    private static readonly TimeSpan RollbackMoveRetryDelay =
        TimeSpan.FromMilliseconds(250);
    private const int FirewallVerificationRetryCount = 10;
    private static readonly TimeSpan FirewallVerificationRetryDelay =
        TimeSpan.FromMilliseconds(200);
    private const string UnavailableFirewallSnapshotDescription =
        "SNAPSHOT_UNAVAILABLE";

    public async Task<SetupOperationResult> DeployAsync(
        SetupRequest request,
        CancellationToken cancellationToken)
    {
        var steps = new SetupStepRecorder();
        var processGateEntered = false;
        IDisposable? machineLease = null;
        try
        {
            steps.MarkActiveStage(SetupFailureStage.OperationLock);
            await ProcessDeploymentGate.WaitAsync(cancellationToken);
            processGateEntered = true;
            machineLease = machineDeploymentLock.Acquire();
            return await DeployCoreAsync(request, steps, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            steps.Add(Failed(
                SetupErrorCodes.Cancelled,
                "설치",
                "사용자가 작업을 취소했습니다."));
            return SetupOperationResult.Failure(
                SetupErrorCodes.Cancelled,
                "설치가 취소되었습니다.",
                steps);
        }
        catch (SetupException exception)
        {
            steps.Add(Failed(
                exception.Code,
                "설치",
                exception.Message));
            return SetupOperationResult.Failure(
                exception.Code,
                exception.Message,
                steps);
        }
        catch (Exception exception)
        {
            steps.RecordUnexpectedFailure(exception);
            steps.Add(Failed(
                SetupErrorCodes.Unexpected,
                "설치",
                "예상하지 못한 Windows 오류로 설치를 완료하지 못했습니다."));
            return SetupOperationResult.Failure(
                SetupErrorCodes.Unexpected,
                "예상하지 못한 Windows 오류로 설치를 완료하지 못했습니다.",
                steps);
        }
        finally
        {
            machineLease?.Dispose();
            if (processGateEntered)
            {
                ProcessDeploymentGate.Release();
            }
        }
    }

    public PendingRecoveryInspection InspectPendingRecovery()
    {
        var journalStore = new DeploymentJournalStore(fileSystem, paths);
        if (!journalStore.Exists)
        {
            return PendingRecoveryInspection.None;
        }

        try
        {
            var pending = journalStore.Read();
            ValidatePendingTransaction(pending);
            var currentService = serviceManager.Capture(SetupConstants.ServiceName);
            fileSystem.ValidateRecoveryPaths(
                paths,
                currentService,
                pending.PreviousService,
                pending.DataDirectoryCreated &&
                !pending.DataDirectoryExistedBefore,
                [pending.StagingDirectory, pending.BackupDirectory, pending.FailedDirectory]);

            return BuildPendingInspection(
                pending,
                currentService,
                canRecover: true,
                SetupErrorCodes.RecoveryRequired,
                "이전 설치 작업이 완료되지 않았습니다. 먼저 이전 상태 복구를 실행하세요.");
        }
        catch (SetupException exception)
        {
            return BuildUnsafePendingInspection(
                journalStore,
                exception.Code,
                exception.Message);
        }
        catch
        {
            return BuildUnsafePendingInspection(
                journalStore,
                SetupErrorCodes.RecoveryRequired,
                "이전 설치 작업의 복구 상태를 안전하게 확인할 수 없습니다. 관리자 확인이 필요합니다.");
        }
    }

    public async Task<SetupOperationResult> RecoverAsync(
        CancellationToken cancellationToken)
    {
        var steps = new SetupStepRecorder();
        var processGateEntered = false;
        IDisposable? machineLease = null;
        try
        {
            steps.MarkActiveStage(SetupFailureStage.OperationLock);
            await ProcessDeploymentGate.WaitAsync(cancellationToken);
            processGateEntered = true;
            machineLease = machineDeploymentLock.Acquire();
            steps.MarkActiveStage(SetupFailureStage.Administrator);
            if (!administratorChecker.IsAdministrator())
            {
                throw new SetupException(
                    SetupErrorCodes.AdministratorRequired,
                    "Agent 복구에는 관리자 권한이 필요합니다.");
            }

            steps.MarkActiveStage(SetupFailureStage.RecoveryJournal);
            var journalStore = new DeploymentJournalStore(fileSystem, paths);
            if (!journalStore.Exists)
            {
                steps.Add(Succeeded(
                    "RECOVERY_NOT_REQUIRED",
                    "이전 상태 복구",
                    "복구가 필요한 이전 설치 작업이 없습니다."));
                return SetupOperationResult.Success(
                    "복구가 필요한 이전 설치 작업이 없습니다.",
                    steps);
            }

            steps.MarkActiveStage(SetupFailureStage.Recovery);
            await RecoverPendingTransactionAsync(
                journalStore,
                steps,
                cancellationToken);
            if (journalStore.Exists)
            {
                RecordRemainingJournalFailure(journalStore, steps);
                throw new SetupException(
                    SetupErrorCodes.RollbackFailed,
                    "이전 설치 상태 복구 뒤에도 작업 기록이 남아 있어 설치를 계속할 수 없습니다.");
            }

            return SetupOperationResult.Success(
                "이전 상태 복구가 완료되었습니다. 설치 / 업데이트를 다시 실행할 수 있습니다.",
                steps);
        }
        catch (OperationCanceledException)
        {
            steps.Add(Failed(
                SetupErrorCodes.Cancelled,
                "이전 상태 복구",
                "사용자가 복구 작업을 취소했습니다."));
            return SetupOperationResult.Failure(
                SetupErrorCodes.Cancelled,
                "이전 상태 복구가 취소되었습니다.",
                steps);
        }
        catch (SetupException exception)
        {
            var hasRollbackStageFailure = steps.Any(step =>
                step.State == SetupStepState.Failed &&
                IsRollbackStageFailure(step.Code));
            if (!hasRollbackStageFailure &&
                !steps.Any(step =>
                    step.State == SetupStepState.Failed &&
                    string.Equals(step.Code, exception.Code, StringComparison.Ordinal)))
            {
                steps.Add(Failed(
                    exception.Code,
                    "이전 상태 복구",
                    exception.Message));
            }

            var inspection = InspectPendingRecovery();
            var rollbackFailureCodes = inspection.RollbackFailureCodes
                .Concat(steps
                    .Where(step =>
                        step.State == SetupStepState.Failed &&
                        IsRollbackStageFailure(step.Code))
                    .Select(step => step.Code))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var rollbackFailed =
                exception.Code == SetupErrorCodes.RollbackFailed ||
                rollbackFailureCodes.Length > 0;
            var finalCode = rollbackFailed
                ? SetupErrorCodes.RollbackFailed
                : exception.Code;
            var finalMessage = rollbackFailed
                ? "이전 설치 상태를 완전히 복구하지 못했습니다. 작업 기록과 백업을 보존했습니다."
                : exception.Message;
            return SetupOperationResult.Failure(
                finalCode,
                finalMessage,
                steps) with
            {
                PrimaryFailureCode = inspection.PrimaryFailureCode,
                PrimaryFailureMessage = inspection.PrimaryFailureMessage,
                RollbackFailureCodes = rollbackFailureCodes,
                AgentHealthCode = inspection.AgentHealthCode,
                AgentRestartObserved = inspection.AgentRestartObserved,
                AgentServiceRunningObserved =
                    inspection.AgentServiceRunningObserved,
                AgentListenerOwnedObserved =
                    inspection.AgentListenerOwnedObserved,
                AgentHttpAttemptCount = inspection.AgentHttpAttemptCount,
                AgentLastTransportPhase = inspection.AgentLastTransportPhase
            };
        }
        catch (Exception exception)
        {
            steps.RecordUnexpectedFailure(exception);
            steps.Add(Failed(
                SetupErrorCodes.Unexpected,
                "이전 상태 복구",
                "예상하지 못한 Windows 오류로 복구를 완료하지 못했습니다. 작업 기록과 설치 자료는 보존됩니다."));
            PendingRecoveryInspection inspection;
            try
            {
                inspection = InspectPendingRecovery();
            }
            catch
            {
                inspection = new PendingRecoveryInspection(
                    true,
                    false,
                    SetupErrorCodes.Unexpected,
                    "이전 설치 작업 상태를 확인할 수 없습니다. 관리자 확인이 필요합니다.")
                {
                    EvidenceStateKnown = false
                };
            }

            return SetupOperationResult.Failure(
                SetupErrorCodes.Unexpected,
                "예상하지 못한 Windows 오류로 이전 상태 복구를 완료하지 못했습니다.",
                steps) with
            {
                PrimaryFailureCode = inspection.PrimaryFailureCode,
                PrimaryFailureMessage = inspection.PrimaryFailureMessage,
                RollbackFailureCodes = inspection.RollbackFailureCodes,
                AgentHealthCode = inspection.AgentHealthCode,
                AgentRestartObserved = inspection.AgentRestartObserved,
                AgentServiceRunningObserved =
                    inspection.AgentServiceRunningObserved,
                AgentListenerOwnedObserved =
                    inspection.AgentListenerOwnedObserved,
                AgentHttpAttemptCount = inspection.AgentHttpAttemptCount,
                AgentLastTransportPhase = inspection.AgentLastTransportPhase
            };
        }
        finally
        {
            machineLease?.Dispose();
            if (processGateEntered)
            {
                ProcessDeploymentGate.Release();
            }
        }
    }

    private async Task<SetupOperationResult> DeployCoreAsync(
        SetupRequest request,
        SetupStepRecorder steps,
        CancellationToken cancellationToken)
    {
        string? stagingDirectory = null;
        string? backupDirectory = null;
        string? failedDirectory = null;
        var installMovedToBackup = false;
        var stagingActivated = false;
        var dataDirectoryExistedBefore = false;
        var dataDirectoryCreated = false;
        var mutationStarted = false;
        var currentTransactionOwned = false;
        ServiceSnapshot? previousService = null;
        FirewallRuleSnapshot? previousHttpsFirewall = null;
        FirewallRuleSnapshot? previousHttpFirewall = null;
        AgentHealthProbeResult? agentHealth = null;
        var firewallConfigurationEligible = true;
        var firewallWarningPending = false;
        var firewallRemoteAccessConfirmed = false;
        var firewallMutationStarted = false;
        var journalStore = new DeploymentJournalStore(fileSystem, paths);
        DeploymentJournal? journal = null;

        try
        {
            steps.MarkActiveStage(SetupFailureStage.Administrator);
            if (!administratorChecker.IsAdministrator())
            {
                throw new SetupException(
                    SetupErrorCodes.AdministratorRequired,
                    "Agent 서비스 설치에는 관리자 권한이 필요합니다.");
            }

            steps.MarkActiveStage(SetupFailureStage.RecoveryJournal);
            if (journalStore.Exists)
            {
                throw new SetupException(
                    SetupErrorCodes.RecoveryRequired,
                    "이전 설치 작업이 완료되지 않았습니다. 먼저 이전 상태 복구를 실행하세요.");
            }

            steps.MarkActiveStage(SetupFailureStage.Input);
            SetupDiagnosticsService.ValidateInput(request);
            steps.MarkActiveStage(SetupFailureStage.PackageValidation);
            var package = packageValidator.Validate(paths.PackageDirectory);
            steps.Add(Succeeded(
                "PACKAGE_VALID",
                "패키지 확인",
                $"Agent {package.Version} 파일 무결성이 정상입니다."));

            var transactionId = Guid.NewGuid().ToString("N");
            stagingDirectory = $"{paths.InstallDirectory}.__staging_{transactionId}";
            backupDirectory = $"{paths.InstallDirectory}.__backup_{transactionId}";
            failedDirectory = $"{paths.InstallDirectory}.__failed_{transactionId}";

            steps.MarkActiveStage(SetupFailureStage.FileSystem);
            previousService = serviceManager.Capture(SetupConstants.ServiceName);
            ValidateExistingServiceContract(previousService);
            dataDirectoryExistedBefore =
                fileSystem.DirectoryExists(paths.DataDirectory);
            fileSystem.ValidateDeploymentPaths(
                paths,
                previousService,
                [stagingDirectory, backupDirectory, failedDirectory]);
            if (!fileSystem.CanCreateUnder(paths.InstallDirectory) ||
                !fileSystem.CanCreateUnder(paths.DataDirectory))
            {
                throw new SetupException(
                    SetupErrorCodes.PathNotWritable,
                    "Program Files 또는 ProgramData 설치 경로를 사용할 수 없습니다.");
            }

            steps.MarkActiveStage(SetupFailureStage.Firewall);
            try
            {
                previousHttpsFirewall = firewallManager.Capture(
                    SetupConstants.FirewallRuleName);
                previousHttpFirewall = firewallManager.Capture(
                    SetupConstants.LegacyFirewallRuleName);
                var firewallAssessment = firewallManager.AssertSecurityGate(
                    SetupConstants.HttpsPort,
                    paths.AgentExecutablePath);
                SetupDiagnosticsService.AddFirewallWarnings(
                    steps,
                    firewallAssessment);
            }
            catch (SetupException exception) when (
                exception.Code == SetupErrorCodes.FirewallFailed)
            {
                firewallConfigurationEligible = false;
                firewallWarningPending = true;
                previousHttpsFirewall = UnavailableFirewallSnapshot(
                    SetupConstants.FirewallRuleName);
                previousHttpFirewall = UnavailableFirewallSnapshot(
                    SetupConstants.LegacyFirewallRuleName);
            }

            journal = new DeploymentJournal(
                DeploymentJournalStore.CurrentFormatVersion,
                transactionId,
                "prepared",
                package.Version,
                stagingDirectory,
                backupDirectory,
                failedDirectory,
                false,
                false,
                false,
                dataDirectoryExistedBefore,
                false,
                previousService,
                previousHttpsFirewall,
                previousHttpFirewall);
            currentTransactionOwned = true;

            steps.MarkActiveStage(SetupFailureStage.Configuration);
            var existingConfiguration = fileSystem.FileExists(paths.ProductionConfigurationPath)
                ? fileSystem.ReadAllText(paths.ProductionConfigurationPath)
                : null;
            var configuration = AgentConfigurationFactory.Create(
                paths.DataDirectory,
                request.TargetCidrs,
                request.ViewerIpv4,
                existingConfiguration);

            steps.MarkActiveStage(SetupFailureStage.FileStaging);
            fileSystem.CreateDirectory(Path.GetDirectoryName(paths.InstallDirectory)!);
            fileSystem.CreateDirectory(stagingDirectory);
            fileSystem.EnsureDirectoryAccess(
                stagingDirectory,
                DirectoryAccessKind.AdministratorOnly);
            foreach (var runtimeFile in package.VerifiedFiles.Where(IsAgentRuntimeFile))
            {
                fileSystem.CopyFile(
                    runtimeFile.Path,
                    Path.Combine(stagingDirectory, runtimeFile.Name),
                    overwrite: false);
            }
            fileSystem.CopyFile(
                package.ManifestPath,
                Path.Combine(stagingDirectory, SetupConstants.ManifestFileName),
                overwrite: false);

            var stagedHash = fileSystem.ComputeSha256(
                Path.Combine(stagingDirectory, SetupConstants.AgentExecutableName));
            if (!string.Equals(stagedHash, package.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new SetupException(
                    SetupErrorCodes.PackageHashMismatch,
                    "보호된 임시 폴더로 복사한 Agent 파일의 무결성 확인에 실패했습니다.");
            }

            VerifyStagedRuntime(package, stagingDirectory);

            fileSystem.WriteAllTextAtomic(
                Path.Combine(stagingDirectory, "appsettings.Production.json"),
                configuration);
            journalStore.Write(journal);
            steps.Add(Succeeded(
                "PACKAGE_STAGED",
                "설치 준비",
                "Agent 파일과 설정을 보호된 임시 위치에 준비했습니다."));

            cancellationToken.ThrowIfCancellationRequested();
            mutationStarted = true;
            steps.MarkActiveStage(SetupFailureStage.ServiceStop);
            journal = journal with
            {
                Stage = "service-stop-pending",
                MutationStarted = true
            };
            journalStore.Write(journal);
            // Stop every existing service state, including START_PENDING and
            // STOP_PENDING. Capture.Running is deliberately strict and those
            // pending states can still own a live process/file handle.
            if (previousService.Exists)
            {
                serviceManager.Stop(SetupConstants.ServiceName, TimeSpan.FromSeconds(20));
            }

            steps.MarkActiveStage(SetupFailureStage.FileActivation);
            if (fileSystem.DirectoryExists(paths.InstallDirectory))
            {
                installMovedToBackup = true;
                journal = journal with
                {
                    Stage = "backup-move-pending",
                    InstallMovedToBackup = true
                };
                journalStore.Write(journal);
                fileSystem.MoveDirectory(paths.InstallDirectory, backupDirectory);
                fileSystem.EnsureDirectoryAccess(
                    backupDirectory,
                    DirectoryAccessKind.AdministratorOnly);
            }

            VerifyStagedRuntime(package, stagingDirectory);
            stagingActivated = true;
            journal = journal with
            {
                Stage = "activation-pending",
                StagingActivated = true
            };
            journalStore.Write(journal);
            fileSystem.MoveDirectory(stagingDirectory, paths.InstallDirectory);

            steps.MarkActiveStage(SetupFailureStage.ServiceConfiguration);
            var serviceBinaryPath = $"\"{paths.AgentExecutablePath}\" --service";
            serviceManager.InstallOrUpdate(
                SetupConstants.ServiceName,
                SetupConstants.ServiceDisplayName,
                serviceBinaryPath,
                $@"NT SERVICE\{SetupConstants.ServiceName}");
            // A recovery restart during readiness can replace the service PID
            // and race the rollback file moves. Recovery is restored/enabled
            // only after this version has passed the bounded readiness gate.
            serviceManager.DisableRecovery(SetupConstants.ServiceName);
            if (!dataDirectoryExistedBefore)
            {
                dataDirectoryCreated = true;
                journal = journal with
                {
                    Stage = "data-directory-create-pending",
                    DataDirectoryCreated = true
                };
                journalStore.Write(journal);
            }

            fileSystem.CreateDirectory(paths.DataDirectory);
            fileSystem.EnsureDirectoryAccess(
                paths.InstallDirectory,
                DirectoryAccessKind.ProgramReadExecute);
            fileSystem.EnsureDirectoryAccess(
                paths.DataDirectory,
                DirectoryAccessKind.AgentDataModify);
            journal = journal with { Stage = "service-configured" };
            journalStore.Write(journal);
            steps.Add(Succeeded(
                "SERVICE_CONFIGURED",
                "서비스 구성",
                "창 없는 Agent 자동 시작 서비스를 구성했습니다."));

            steps.MarkActiveStage(SetupFailureStage.ServiceStart);
            serviceManager.Start(SetupConstants.ServiceName, TimeSpan.FromSeconds(30));
            journal = journal with { Stage = "service-started" };
            journalStore.Write(journal);
            steps.Add(Succeeded(
                "SERVICE_STARTED",
                "Agent 시작",
                "Agent 서비스가 시작되어 로컬 HTTPS 준비 상태를 확인합니다."));
            steps.MarkActiveStage(SetupFailureStage.Readiness);
            agentHealth = await healthProbe.WaitUntilReadyAsync(
                new Uri("https://127.0.0.1:18443/health/ready"),
                package.Version,
                () => serviceManager.Capture(SetupConstants.ServiceName),
                TimeSpan.FromSeconds(60),
                cancellationToken);
            steps.AddSafeDecisionCode(AgentHealthDecisionCode(agentHealth.Value.Code));
            journal = journal with
            {
                AgentHealthCode = agentHealth.Value.Code.ToString(),
                AgentRestartObserved = agentHealth.Value.RestartObserved,
                AgentServiceRunningObserved =
                    agentHealth.Value.ServiceRunningObserved,
                AgentListenerOwnedObserved =
                    agentHealth.Value.ListenerOwnedObserved,
                AgentHttpAttemptCount = agentHealth.Value.HttpAttemptCount,
                AgentLastTransportPhase = agentHealth.Value.LastTransportPhase
            };
            journalStore.Write(journal);
            if (!agentHealth.Value.Ready)
            {
                throw new SetupException(
                    SetupErrorCodes.HealthFailed,
                    AgentHealthFailureMessage(agentHealth.Value));
            }

            steps.Add(Succeeded(
                "AGENT_READY",
                "Agent 확인",
                agentHealth.Value.RestartObserved
                    ? "Agent 서비스가 시작 중 다시 실행된 뒤 정상 준비 상태가 되었습니다."
                    : "Agent 서비스가 정상적으로 실행되고 있습니다."));

            steps.MarkActiveStage(SetupFailureStage.ServiceConfiguration);
            serviceManager.ConfigureRecovery(SetupConstants.ServiceName);

            if (firewallWarningPending)
            {
                AddFirewallRemoteAccessWarning(
                    steps,
                    firewallStateRestored: true);
            }

            if (firewallConfigurationEligible)
            {
                try
                {
                    steps.MarkActiveStage(SetupFailureStage.Firewall);
                    firewallMutationStarted = true;
                    firewallManager.RemoveOwnedRule(SetupConstants.LegacyFirewallRuleName);
                    firewallManager.ApplyViewerRule(
                        SetupConstants.FirewallRuleName,
                        SetupConstants.HttpsPort,
                        request.ViewerIpv4);
                    var firewallVerification = await VerifyViewerFirewallRuleAsync(
                        request.ViewerIpv4,
                        cancellationToken);
                    if (!firewallVerification.IsExact)
                    {
                        steps.AddSafeDecisionCode(
                            firewallVerification.MismatchCode);
                        throw new SetupException(
                            SetupErrorCodes.FirewallFailed,
                            "Viewer 전용 방화벽 규칙을 확인하지 못했습니다.");
                    }

                    _ = firewallManager.AssertSecurityGate(
                        SetupConstants.HttpsPort,
                        paths.AgentExecutablePath);
                    journal = journal with { Stage = "firewall-configured" };
                    journalStore.Write(journal);
                    firewallRemoteAccessConfirmed = true;

                    steps.Add(Succeeded(
                        "FIREWALL_CONFIGURED",
                        "방화벽 구성",
                        $"제품 소유 Viewer {request.ViewerIpv4}/32 HTTPS/18443 규칙을 구성했고 Agent 원격 업무 API도 동일한 Viewer IP만 허용합니다."));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (SetupException exception) when (
                    exception.Code == SetupErrorCodes.FirewallFailed)
                {
                    var firewallStateRestored =
                        !firewallMutationStarted ||
                        TryRestoreFirewallSnapshotsBestEffort(
                            previousHttpsFirewall,
                            previousHttpFirewall);
                    if (firewallStateRestored)
                    {
                        firewallMutationStarted = false;
                    }
                    AddFirewallRemoteAccessWarning(
                        steps,
                        firewallStateRestored);
                }
            }

            // Health success is the transaction commit boundary. Backup cleanup
            // must never turn a working installation into a rollback attempt
            // because recursive deletion may already be partially complete.
            steps.MarkActiveStage(SetupFailureStage.CommitCleanup);
            journal = journal with { Stage = "committed" };
            journalStore.Write(journal);
            var committedDirectoriesClean = true;
            foreach (var target in new[]
                     {
                         (
                             Path: journal.StagingDirectory,
                             Code: SetupErrorCodes.RollbackStagingCleanupFailed,
                             Label: "임시 설치 자료 정리"),
                         (
                             Path: journal.BackupDirectory,
                             Code: SetupErrorCodes.RollbackBackupCleanupFailed,
                             Label: "이전 파일 정리"),
                         (
                             Path: journal.FailedDirectory,
                             Code: SetupErrorCodes.RollbackFailedDirectoryCleanupFailed,
                             Label: "실패 설치 자료 정리")
                     })
            {
                if (await TryDeleteEvidenceDirectoryAsync(
                        target.Path,
                        CancellationToken.None))
                {
                    continue;
                }

                committedDirectoriesClean = false;
                steps.Add(new SetupStepResult(
                    target.Code,
                    target.Label,
                    SetupStepState.Information,
                    "설치는 완료됐지만 이전 설치 작업의 정리 자료 일부가 남아 있습니다."));
            }

            if (!committedDirectoriesClean)
            {
                steps.Add(new SetupStepResult(
                    "BACKUP_CLEANUP_PENDING",
                    "이전 파일 정리",
                    SetupStepState.Information,
                    "설치는 완료됐지만 이전 설치 작업의 정리 자료 일부가 남았습니다. Agent 동작에는 영향이 없습니다."));
            }
            else if (!await TryDeleteJournalAsync(
                         journalStore,
                         CancellationToken.None))
            {
                steps.Add(new SetupStepResult(
                    SetupErrorCodes.RollbackJournalCleanupFailed,
                    "작업 기록 정리",
                    SetupStepState.Information,
                    "설치는 완료됐지만 완료된 작업 기록이 남아 있습니다."));
                steps.Add(new SetupStepResult(
                    "JOURNAL_CLEANUP_PENDING",
                    "작업 기록 정리",
                    SetupStepState.Information,
                    "설치는 완료됐지만 완료된 작업 기록이 남았습니다. 다음 실행에서 안전하게 정리합니다."));
            }

            return SetupOperationResult.Success(
                firewallRemoteAccessConfirmed
                    ? "Agent 설치 또는 업데이트가 완료되었습니다."
                    : "Agent 설치 또는 업데이트가 완료됐지만 원격 Viewer 연결은 확인이 필요합니다.",
                steps) with
            {
                AgentHealthCode = agentHealth.Value.Code.ToString(),
                AgentRestartObserved = agentHealth.Value.RestartObserved,
                AgentServiceRunningObserved =
                    agentHealth.Value.ServiceRunningObserved,
                AgentListenerOwnedObserved =
                    agentHealth.Value.ListenerOwnedObserved,
                AgentHttpAttemptCount = agentHealth.Value.HttpAttemptCount,
                AgentLastTransportPhase = agentHealth.Value.LastTransportPhase
            };
        }
        catch (OperationCanceledException)
        {
            const string primaryCode = SetupErrorCodes.Cancelled;
            const string primaryMessage = "설치가 취소되었습니다.";
            RecordPrimaryFailureBeforeRollback(
                steps,
                primaryCode,
                "설치 취소",
                primaryMessage);
            var rollback = currentTransactionOwned
                ? await TryRollbackAsync(
                    journalStore,
                    journal,
                    previousService,
                    previousHttpsFirewall,
                    previousHttpFirewall,
                    stagingDirectory,
                    backupDirectory,
                    failedDirectory,
                    installMovedToBackup,
                    stagingActivated,
                    dataDirectoryExistedBefore,
                    dataDirectoryCreated,
                    mutationStarted,
                    firewallMutationStarted,
                    primaryCode,
                    primaryMessage,
                    steps,
                    CancellationToken.None)
                : RollbackOutcome.Success;
            return BuildFailedDeploymentResult(
                primaryCode,
                primaryMessage,
                rollback,
                "설치 취소",
                steps,
                agentHealth);
        }
        catch (SetupException exception)
        {
            RecordPrimaryFailureBeforeRollback(
                steps,
                exception.Code,
                "설치 실패",
                exception.Message);
            var rollback = currentTransactionOwned
                ? await TryRollbackAsync(
                    journalStore,
                    journal,
                    previousService,
                    previousHttpsFirewall,
                    previousHttpFirewall,
                    stagingDirectory,
                    backupDirectory,
                    failedDirectory,
                    installMovedToBackup,
                    stagingActivated,
                    dataDirectoryExistedBefore,
                    dataDirectoryCreated,
                    mutationStarted,
                    firewallMutationStarted,
                    exception.Code,
                    exception.Message,
                    steps,
                    CancellationToken.None)
                : RollbackOutcome.Success;
            return BuildFailedDeploymentResult(
                exception.Code,
                exception.Message,
                rollback,
                "설치 실패",
                steps,
                agentHealth);
        }
        catch (Exception exception)
        {
            steps.RecordUnexpectedFailure(exception);
            const string primaryCode = SetupErrorCodes.Unexpected;
            const string primaryMessage =
                "예상하지 못한 오류로 설치를 완료하지 못했습니다.";
            RecordPrimaryFailureBeforeRollback(
                steps,
                primaryCode,
                "설치 실패",
                primaryMessage);
            var rollback = currentTransactionOwned
                ? await TryRollbackAsync(
                    journalStore,
                    journal,
                    previousService,
                    previousHttpsFirewall,
                    previousHttpFirewall,
                    stagingDirectory,
                    backupDirectory,
                    failedDirectory,
                    installMovedToBackup,
                    stagingActivated,
                    dataDirectoryExistedBefore,
                    dataDirectoryCreated,
                    mutationStarted,
                    firewallMutationStarted,
                    primaryCode,
                    primaryMessage,
                    steps,
                    CancellationToken.None)
                : RollbackOutcome.Success;
            return BuildFailedDeploymentResult(
                primaryCode,
                primaryMessage,
                rollback,
                "설치 실패",
                steps,
                agentHealth);
        }
    }

    private async Task<FirewallRuleVerificationResult> VerifyViewerFirewallRuleAsync(
        string viewerIpv4,
        CancellationToken cancellationToken)
    {
        FirewallRuleVerificationResult verification = default;
        for (var attempt = 0;
             attempt <= FirewallVerificationRetryCount;
             attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = firewallManager.Capture(
                SetupConstants.FirewallRuleName);
            verification = FirewallRuleVerifier.Evaluate(
                snapshot,
                SetupConstants.HttpsPort,
                viewerIpv4);
            if (verification.IsExact)
            {
                return verification;
            }

            if (attempt < FirewallVerificationRetryCount)
            {
                await Task.Delay(
                    FirewallVerificationRetryDelay,
                    cancellationToken);
            }
        }

        return verification;
    }

    private bool TryRestoreFirewallSnapshotsBestEffort(
        FirewallRuleSnapshot? previousHttpsFirewall,
        FirewallRuleSnapshot? previousHttpFirewall)
    {
        var restored = true;
        foreach (var snapshot in new[]
                 {
                     previousHttpsFirewall,
                     previousHttpFirewall
                 })
        {
            if (snapshot is null)
            {
                continue;
            }

            try
            {
                firewallManager.Restore(snapshot);
            }
            catch
            {
                restored = false;
            }
        }

        return restored;
    }

    private static FirewallRuleSnapshot UnavailableFirewallSnapshot(
        string ruleName) =>
        FirewallRuleSnapshot.Missing(ruleName) with
        {
            Description = UnavailableFirewallSnapshotDescription
        };

    private static bool FirewallSnapshotsAreUnavailable(
        FirewallRuleSnapshot? previousHttpsFirewall,
        FirewallRuleSnapshot? previousHttpFirewall) =>
        string.Equals(
            previousHttpsFirewall?.Description,
            UnavailableFirewallSnapshotDescription,
            StringComparison.Ordinal) ||
        string.Equals(
            previousHttpFirewall?.Description,
            UnavailableFirewallSnapshotDescription,
            StringComparison.Ordinal);

    private static void AddFirewallRemoteAccessWarning(
        SetupStepRecorder steps,
        bool firewallStateRestored)
    {
        steps.AddSafeDecisionCode(
            SetupErrorCodes.FirewallRemoteAccessUnconfirmed);
        steps.Add(new SetupStepResult(
            SetupErrorCodes.FirewallRemoteAccessUnconfirmed,
            "원격 Viewer 연결",
            SetupStepState.Warning,
            firewallStateRestored
                ? "Agent 로컬 HTTPS는 정상입니다. Viewer 전용 방화벽 규칙을 자동 구성하지 못했으므로 원격 연결이 되지 않으면 회사 방화벽 정책을 확인하세요."
                : "Agent 로컬 HTTPS는 정상입니다. Viewer 전용 방화벽 규칙 구성과 변경 전 상태 복구 여부를 확인하지 못했으므로 회사 방화벽 정책을 확인하세요."));
    }

    private async Task<RollbackOutcome> TryRollbackAsync(
        DeploymentJournalStore journalStore,
        DeploymentJournal? journal,
        ServiceSnapshot? previousService,
        FirewallRuleSnapshot? previousHttpsFirewall,
        FirewallRuleSnapshot? previousHttpFirewall,
        string? stagingDirectory,
        string? backupDirectory,
        string? failedDirectory,
        bool installMovedToBackup,
        bool stagingActivated,
        bool dataDirectoryExistedBefore,
        bool dataDirectoryCreated,
        bool mutationStarted,
        bool firewallMutationStarted,
        string primaryFailureCode,
        string primaryFailureMessage,
        SetupStepRecorder steps,
        CancellationToken cleanupCancellationToken)
    {
        var failureCodes = new List<string>();
        var installExists = false;
        var backupExists = false;
        var failedExists = false;
        var stagingExists = false;
        var dataDirectoryExists = false;

        try
        {
            installExists = fileSystem.DirectoryExists(paths.InstallDirectory);
            backupExists = backupDirectory is not null &&
                           fileSystem.DirectoryExists(backupDirectory);
            failedExists = failedDirectory is not null &&
                           fileSystem.DirectoryExists(failedDirectory);
            stagingExists = stagingDirectory is not null &&
                            fileSystem.DirectoryExists(stagingDirectory);
            dataDirectoryExists = fileSystem.DirectoryExists(paths.DataDirectory);
            if ((!installMovedToBackup && backupExists) ||
                (!stagingActivated && failedExists) ||
                (stagingExists && failedExists) ||
                (dataDirectoryExistedBefore && !dataDirectoryExists) ||
                (!dataDirectoryExistedBefore &&
                 !dataDirectoryCreated &&
                 dataDirectoryExists) ||
                (installMovedToBackup &&
                 !stagingActivated &&
                 installExists &&
                 backupExists))
            {
                throw new InvalidOperationException();
            }
        }
        catch
        {
            AddRollbackFailure(
                failureCodes,
                steps,
                SetupErrorCodes.RollbackStateMismatch,
                "복구 상태 확인",
                "설치 폴더 상태가 작업 기록과 일치하지 않습니다. 설치 폴더와 백업 자료를 보존했습니다.");
        }

        if (failureCodes.Count > 0)
        {
            PersistRollbackFailureMetadata(
                journalStore,
                journal,
                primaryFailureCode,
                primaryFailureMessage,
                failureCodes,
                steps);
            return RollbackOutcome.Failed(failureCodes);
        }

        var serviceStopped = true;
        if (mutationStarted)
        {
            try
            {
                var current = serviceManager.Capture(SetupConstants.ServiceName);
                if (current.Exists)
                {
                    serviceManager.Stop(
                        SetupConstants.ServiceName,
                        TimeSpan.FromSeconds(20));
                }
            }
            catch
            {
                serviceStopped = false;
                AddRollbackFailure(
                    failureCodes,
                    steps,
                    SetupErrorCodes.RollbackServiceStopFailed,
                    "Agent 중지",
                    "Agent 서비스를 중지하지 못해 파일 복구를 시작하지 않았습니다.");
            }
        }

        var filesRestored = !mutationStarted;
        var dataCleanupCompleted = !mutationStarted;
        var filesValidated = !mutationStarted;
        if (serviceStopped && mutationStarted)
        {
            try
            {
                // A previous rollback may have moved backup -> install and then
                // failed while restoring the install ACL. In that state the
                // failed new version and restored old version both exist.
                var backupWasAlreadyRestored = installMovedToBackup &&
                                               installExists &&
                                               !backupExists &&
                                               (failedExists || stagingExists);
                var rollbackTopologyIsAmbiguous = installMovedToBackup &&
                                                  stagingActivated &&
                                                  installExists &&
                                                  !backupExists &&
                                                  !failedExists &&
                                                  !stagingExists;
                if (rollbackTopologyIsAmbiguous)
                {
                    throw new InvalidOperationException();
                }

                if (stagingActivated && installExists && !backupWasAlreadyRestored)
                {
                    if (failedDirectory is null || failedExists)
                    {
                        throw new InvalidOperationException();
                    }

                    await MoveDirectoryForRollbackAsync(
                        paths.InstallDirectory,
                        failedDirectory,
                        cleanupCancellationToken);
                    installExists = false;
                    failedExists = true;
                    fileSystem.EnsureDirectoryAccess(
                        failedDirectory,
                        DirectoryAccessKind.AdministratorOnly);
                }

                if (installMovedToBackup)
                {
                    if (backupDirectory is null)
                    {
                        throw new InvalidOperationException();
                    }
                    if (backupExists)
                    {
                        if (installExists)
                        {
                            throw new InvalidOperationException();
                        }

                        await MoveDirectoryForRollbackAsync(
                            backupDirectory,
                            paths.InstallDirectory,
                            cleanupCancellationToken);
                        installExists = true;
                        backupExists = false;
                    }
                    if (!installExists)
                    {
                        throw new InvalidOperationException();
                    }

                    fileSystem.EnsureDirectoryAccess(
                        paths.InstallDirectory,
                        DirectoryAccessKind.ProgramReadExecute);
                }
                else if (stagingActivated && installExists)
                {
                    throw new InvalidOperationException();
                }

                filesRestored = true;
            }
            catch
            {
                AddRollbackFailure(
                    failureCodes,
                    steps,
                    SetupErrorCodes.RollbackFileRestoreFailed,
                    "프로그램 파일 복구",
                    "이전 프로그램 파일과 접근 권한을 완전히 복구하지 못했습니다.");
            }

            if (filesRestored)
            {
                dataCleanupCompleted = true;
                try
                {
                    if (dataDirectoryCreated &&
                        !dataDirectoryExistedBefore &&
                        fileSystem.DirectoryExists(paths.DataDirectory))
                    {
                        var currentService = serviceManager.Capture(
                            SetupConstants.ServiceName);
                        fileSystem.ValidateRecoveryPaths(
                            paths,
                            currentService,
                            previousService ?? ServiceSnapshot.Missing,
                            allowFreshCreatedDataCleanup: true,
                            new[] { stagingDirectory, backupDirectory, failedDirectory }
                                .Where(path => path is not null)
                                .Select(path => path!)
                                .ToArray());
                        fileSystem.DeleteDirectory(
                            paths.DataDirectory,
                            recursive: true);
                    }
                }
                catch
                {
                    dataCleanupCompleted = false;
                    AddRollbackFailure(
                        failureCodes,
                        steps,
                        SetupErrorCodes.RollbackDataCleanupFailed,
                        "데이터 폴더 복구",
                        "이번 설치에서 만든 데이터 폴더를 안전하게 정리하지 못했습니다.");
                }
            }

            if (filesRestored && dataCleanupCompleted)
            {
                try
                {
                    var restoredInstallExists =
                        fileSystem.DirectoryExists(paths.InstallDirectory);
                    var restoredBackupExists =
                        backupDirectory is not null &&
                        fileSystem.DirectoryExists(backupDirectory);
                    var restoredDataExists =
                        fileSystem.DirectoryExists(paths.DataDirectory);
                    // Before the backup move, an interrupted upgrade still has
                    // the previous Agent in the canonical install directory.
                    // A fresh install has no previous service and therefore no
                    // install directory to preserve.
                    var expectedInstallExists =
                        installMovedToBackup ||
                        previousService is { Exists: true };
                    var expectedDataExists = dataDirectoryExistedBefore;
                    if (restoredInstallExists != expectedInstallExists ||
                        restoredBackupExists ||
                        restoredDataExists != expectedDataExists)
                    {
                        throw new InvalidOperationException();
                    }

                    var currentService = serviceManager.Capture(
                        SetupConstants.ServiceName);
                    fileSystem.ValidateRecoveryPaths(
                        paths,
                        currentService,
                        previousService ?? ServiceSnapshot.Missing,
                        allowFreshCreatedDataCleanup: false,
                        new[] { stagingDirectory, backupDirectory, failedDirectory }
                            .Where(path => path is not null)
                            .Select(path => path!)
                            .ToArray());
                    filesValidated = true;
                }
                catch
                {
                    AddRollbackFailure(
                        failureCodes,
                        steps,
                        SetupErrorCodes.RollbackFileRestoreFailed,
                        "복구 결과 확인",
                        "복구된 프로그램 파일 또는 접근 권한을 확인하지 못했습니다.");
                }
            }
        }

        var serviceRestored = !mutationStarted;
        if (mutationStarted &&
            serviceStopped &&
            filesRestored &&
            dataCleanupCompleted &&
            filesValidated)
        {
            try
            {
                if (previousService is not null)
                {
                    serviceManager.Restore(
                        SetupConstants.ServiceName,
                        previousService);
                }
                serviceRestored = true;
            }
            catch
            {
                AddRollbackFailure(
                    failureCodes,
                    steps,
                    SetupErrorCodes.RollbackServiceRestoreFailed,
                    "Agent 서비스 복구",
                    "이전 Agent 서비스 구성을 복구하지 못했습니다.");
            }
        }

        // The two product-owned firewall rules are independent resources.
        // Always try both, even when one restore fails.
        var httpsFirewallRestored = !firewallMutationStarted;
        var legacyFirewallRestored = !firewallMutationStarted;
        if (firewallMutationStarted)
        {
            try
            {
                if (previousHttpsFirewall is not null)
                {
                    firewallManager.Restore(previousHttpsFirewall);
                }
                httpsFirewallRestored = true;
            }
            catch
            {
                AddRollbackFailure(
                    failureCodes,
                    steps,
                    SetupErrorCodes.RollbackHttpsFirewallRestoreFailed,
                    "HTTPS 방화벽 복구",
                    "기존 HTTPS 방화벽 규칙을 복구하지 못했습니다.");
            }

            try
            {
                if (previousHttpFirewall is not null)
                {
                    firewallManager.Restore(previousHttpFirewall);
                }
                legacyFirewallRestored = true;
            }
            catch
            {
                AddRollbackFailure(
                    failureCodes,
                    steps,
                    SetupErrorCodes.RollbackLegacyFirewallRestoreFailed,
                    "이전 HTTP 방화벽 복구",
                    "기존 HTTP 방화벽 규칙을 복구하지 못했습니다.");
            }
        }

        var authoritativeRestorationCompleted =
            serviceStopped &&
            filesRestored &&
            dataCleanupCompleted &&
            filesValidated &&
            serviceRestored &&
            httpsFirewallRestored &&
            legacyFirewallRestored;
        var rollbackMarkerWritten = !stagingActivated;
        if (authoritativeRestorationCompleted &&
            stagingActivated &&
            journal is not null)
        {
            try
            {
                journalStore.Write(journal with
                {
                    FormatVersion = DeploymentJournalStore.CurrentFormatVersion,
                    Stage = RollbackCompletedStage,
                    InstallMovedToBackup = installMovedToBackup,
                    StagingActivated = true,
                    DataDirectoryCreated = dataDirectoryCreated,
                    PrimaryFailureCode = primaryFailureCode,
                    PrimaryFailureMessage = primaryFailureMessage,
                    RollbackFailureCodes = []
                });
                rollbackMarkerWritten = true;
            }
            catch
            {
                AddRollbackFailure(
                    failureCodes,
                    steps,
                    SetupErrorCodes.RollbackJournalWriteFailed,
                    "복구 기록 저장",
                    "복구 완료 기록을 안전하게 저장하지 못했습니다.");
            }
        }

        if (authoritativeRestorationCompleted && rollbackMarkerWritten)
        {
            if (stagingDirectory is not null &&
                !await TryDeleteEvidenceDirectoryAsync(
                    stagingDirectory,
                    cleanupCancellationToken))
            {
                AddEvidenceCleanupFailure(
                    failureCodes,
                    steps,
                    SetupErrorCodes.RollbackStagingCleanupFailed,
                    "임시 설치 자료 정리",
                    "복구는 완료됐지만 임시 설치 자료를 정리하지 못했습니다.");
            }

            if (backupDirectory is not null &&
                !await TryDeleteEvidenceDirectoryAsync(
                    backupDirectory,
                    cleanupCancellationToken))
            {
                AddEvidenceCleanupFailure(
                    failureCodes,
                    steps,
                    SetupErrorCodes.RollbackBackupCleanupFailed,
                    "이전 파일 정리",
                    "복구는 완료됐지만 이전 설치 백업 자료를 정리하지 못했습니다.");
            }

            if (failedDirectory is not null &&
                !await TryDeleteEvidenceDirectoryAsync(
                    failedDirectory,
                    cleanupCancellationToken))
            {
                AddEvidenceCleanupFailure(
                    failureCodes,
                    steps,
                    SetupErrorCodes.RollbackFailedDirectoryCleanupFailed,
                    "실패 설치 자료 정리",
                    "복구는 완료됐지만 실패한 설치 자료를 정리하지 못했습니다.");
            }
        }

        if (failureCodes.Count == 0 && journal is not null)
        {
            if (!await TryDeleteJournalAsync(
                    journalStore,
                    cleanupCancellationToken))
            {
                AddEvidenceCleanupFailure(
                    failureCodes,
                    steps,
                    SetupErrorCodes.RollbackJournalCleanupFailed,
                    "복구 기록 정리",
                    "복구는 완료됐지만 작업 기록을 정리하지 못했습니다.");
            }
        }

        if (failureCodes.Count > 0)
        {
            PersistRollbackFailureMetadata(
                journalStore,
                journal,
                primaryFailureCode,
                primaryFailureMessage,
                failureCodes,
                steps);
            return RollbackOutcome.Failed(failureCodes);
        }

        steps.Add(new SetupStepResult(
            "ROLLBACK_COMPLETED",
            "이전 상태 복구",
            SetupStepState.Succeeded,
            "설치 전 프로그램·서비스·방화벽·환경 상태로 복구했습니다."));
        return RollbackOutcome.Success;
    }

    private static SetupOperationResult BuildFailedDeploymentResult(
        string primaryCode,
        string primaryMessage,
        RollbackOutcome rollback,
        string label,
        SetupStepRecorder steps,
        AgentHealthProbeResult? agentHealth)
    {
        var finalCode = rollback.Succeeded
            ? primaryCode
            : SetupErrorCodes.RollbackFailed;
        var finalMessage = rollback.Succeeded
            ? primaryMessage
            : "설치에 실패했고 이전 상태를 완전히 복구하지 못했습니다. 관리자 확인이 필요합니다.";
        if (rollback.Succeeded)
        {
            RecordPrimaryFailureBeforeRollback(
                steps,
                primaryCode,
                label,
                primaryMessage);
        }
        else
        {
            // Keep the rollback result as the final failed step while preserving
            // the original failure immediately before rollback starts.
            steps.Add(Failed(finalCode, label, finalMessage));
        }
        return SetupOperationResult.Failure(
            finalCode,
            finalMessage,
            steps) with
        {
            PrimaryFailureCode = primaryCode,
            PrimaryFailureMessage = primaryMessage,
            RollbackFailureCodes = rollback.FailureCodes,
            AgentHealthCode = agentHealth?.Code.ToString(),
            AgentRestartObserved = agentHealth?.RestartObserved ?? false,
            AgentServiceRunningObserved =
                agentHealth?.ServiceRunningObserved ?? false,
            AgentListenerOwnedObserved =
                agentHealth?.ListenerOwnedObserved ?? false,
            AgentHttpAttemptCount = agentHealth?.HttpAttemptCount ?? 0,
            AgentLastTransportPhase =
                agentHealth?.LastTransportPhase ??
                AgentHealthTransportPhase.NotStarted
        };
    }

    private static void RecordPrimaryFailureBeforeRollback(
        SetupStepRecorder steps,
        string code,
        string label,
        string message)
    {
        if (steps.Any(step =>
                step.State == SetupStepState.Failed &&
                string.Equals(step.Code, code, StringComparison.Ordinal)))
        {
            return;
        }

        steps.Add(Failed(code, label, message));
    }

    private async Task MoveDirectoryForRollbackAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= RollbackMoveMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceExists = fileSystem.DirectoryExists(source);
            var destinationExists = fileSystem.DirectoryExists(destination);

            // A prior move may have completed even if Windows reported an
            // error while closing a handle. Accept only the exact completed
            // topology; every ambiguous state fails closed.
            if (!sourceExists && destinationExists)
            {
                return;
            }
            if (!sourceExists || destinationExists)
            {
                throw new InvalidOperationException(
                    "Rollback directory topology is ambiguous.");
            }

            try
            {
                fileSystem.MoveDirectory(source, destination);
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                if (attempt == RollbackMoveMaxAttempts)
                {
                    throw;
                }

                await Task.Delay(
                    RollbackMoveRetryDelay,
                    cancellationToken);
                continue;
            }

            if (!fileSystem.DirectoryExists(source) &&
                fileSystem.DirectoryExists(destination))
            {
                return;
            }

            throw new InvalidOperationException(
                "Rollback directory move did not reach the expected state.");
        }

        throw new IOException("Rollback directory move attempts were exhausted.");
    }

    private static void AddRollbackFailure(
        List<string> failureCodes,
        SetupStepRecorder steps,
        string code,
        string label,
        string message)
    {
        if (!failureCodes.Contains(code, StringComparer.Ordinal))
        {
            failureCodes.Add(code);
            steps.Add(Failed(code, label, message));
        }
    }

    private void PersistRollbackFailureMetadata(
        DeploymentJournalStore journalStore,
        DeploymentJournal? journal,
        string primaryFailureCode,
        string primaryFailureMessage,
        List<string> failureCodes,
        SetupStepRecorder steps)
    {
        if (journal is null)
        {
            return;
        }

        try
        {
            var current = journalStore.Exists
                ? journalStore.Read()
                : journal;
            journalStore.Write(current with
            {
                PrimaryFailureCode = primaryFailureCode,
                PrimaryFailureMessage = primaryFailureMessage,
                RollbackFailureCodes = failureCodes.ToArray(),
                AgentHealthCode =
                    current.AgentHealthCode ?? journal.AgentHealthCode,
                AgentRestartObserved =
                    current.AgentRestartObserved ||
                    journal.AgentRestartObserved,
                AgentServiceRunningObserved =
                    current.AgentServiceRunningObserved ||
                    journal.AgentServiceRunningObserved,
                AgentListenerOwnedObserved =
                    current.AgentListenerOwnedObserved ||
                    journal.AgentListenerOwnedObserved,
                AgentHttpAttemptCount = Math.Max(
                    current.AgentHttpAttemptCount,
                    journal.AgentHttpAttemptCount),
                AgentLastTransportPhase =
                    current.AgentLastTransportPhase !=
                    AgentHealthTransportPhase.NotStarted
                        ? current.AgentLastTransportPhase
                        : journal.AgentLastTransportPhase
            });
        }
        catch
        {
            AddRollbackFailure(
                failureCodes,
                steps,
                SetupErrorCodes.RollbackJournalWriteFailed,
                "복구 기록 저장",
                "복구 실패 정보를 작업 기록에 저장하지 못했습니다.");
        }
    }

    private static void AddEvidenceCleanupFailure(
        List<string> failureCodes,
        SetupStepRecorder steps,
        string targetCode,
        string label,
        string message)
    {
        AddRollbackFailure(
            failureCodes,
            steps,
            targetCode,
            label,
            message);
        AddRollbackFailure(
            failureCodes,
            steps,
            SetupErrorCodes.RollbackEvidenceCleanupFailed,
            "복구 자료 정리",
            "복구 완료 뒤 남은 설치 자료 또는 작업 기록을 정리하지 못했습니다.");
    }

    private void RecordPendingEvidenceCleanupFailure(
        DeploymentJournalStore journalStore,
        DeploymentJournal pending,
        string targetCode,
        string label,
        string message,
        SetupStepRecorder steps)
    {
        AddFailureStep(steps, targetCode, label, message);
        AddFailureStep(
            steps,
            SetupErrorCodes.RollbackEvidenceCleanupFailed,
            "복구 자료 정리",
            "복구 완료 뒤 남은 설치 자료 또는 작업 기록을 정리하지 못했습니다.");

        try
        {
            var current = journalStore.Exists
                ? journalStore.Read()
                : pending;
            var codes = current.RollbackFailureCodes
                .Append(targetCode)
                .Append(SetupErrorCodes.RollbackEvidenceCleanupFailed)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            journalStore.Write(current with
            {
                RollbackFailureCodes = codes
            });
        }
        catch
        {
            var codes = new List<string>();
            AddRollbackFailure(
                codes,
                steps,
                SetupErrorCodes.RollbackJournalWriteFailed,
                "복구 기록 저장",
                "복구 실패 정보를 작업 기록에 저장하지 못했습니다.");
        }
    }

    private void RecordRemainingJournalFailure(
        DeploymentJournalStore journalStore,
        SetupStepRecorder steps)
    {
        AddFailureStep(
            steps,
            SetupErrorCodes.RollbackJournalCleanupFailed,
            "복구 기록 정리",
            "복구가 완료된 뒤 작업 기록이 다시 확인됐습니다.");
        AddFailureStep(
            steps,
            SetupErrorCodes.RollbackEvidenceCleanupFailed,
            "복구 자료 정리",
            "복구 완료 뒤 남은 작업 기록을 정리하지 못했습니다.");

        try
        {
            var pending = journalStore.Read();
            RecordPendingEvidenceCleanupFailure(
                journalStore,
                pending,
                SetupErrorCodes.RollbackJournalCleanupFailed,
                "복구 기록 정리",
                "복구가 완료된 뒤 작업 기록이 다시 확인됐습니다.",
                steps);
        }
        catch
        {
            // The fail-closed journal remains authoritative even when it cannot
            // be read or updated. Do not expose raw filesystem details.
            AddFailureStep(
                steps,
                SetupErrorCodes.RollbackJournalWriteFailed,
                "복구 기록 저장",
                "남은 복구 작업 기록에 실패 정보를 저장하지 못했습니다.");
        }
    }

    private static void AddFailureStep(
        SetupStepRecorder steps,
        string code,
        string label,
        string message)
    {
        if (!steps.Any(step =>
                step.State == SetupStepState.Failed &&
                string.Equals(step.Code, code, StringComparison.Ordinal)))
        {
            steps.Add(Failed(code, label, message));
        }
    }

    private Task<bool> TryDeleteEvidenceDirectoryAsync(
        string directory,
        CancellationToken cancellationToken) =>
        TryDeleteEvidenceAsync(
            () => fileSystem.DirectoryExists(directory),
            () => fileSystem.DeleteDirectory(directory, recursive: true),
            () => fileSystem.EnsureDirectoryAccess(
                directory,
                DirectoryAccessKind.AdministratorOnly),
            cancellationToken);

    private Task<bool> TryDeleteJournalAsync(
        DeploymentJournalStore journalStore,
        CancellationToken cancellationToken) =>
        TryDeleteEvidenceAsync(
            () => journalStore.Exists,
            journalStore.Delete,
            () => fileSystem.EnsureDirectoryAccess(
                paths.OperationsDirectory,
                DirectoryAccessKind.AdministratorOnly),
            cancellationToken);

    private static async Task<bool> TryDeleteEvidenceAsync(
        Func<bool> exists,
        Action delete,
        Action normalizeAccess,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= EvidenceCleanupMaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizeAccessBeforeRetry = false;
            try
            {
                delete();
                if (!exists())
                {
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DeploymentJournalCleanupVerificationException)
            {
                // The delete returned without an access exception, but the
                // journal still exists. Retry without changing its ACL.
            }
            catch (IOException)
            {
                normalizeAccessBeforeRetry = true;
            }
            catch (UnauthorizedAccessException)
            {
                normalizeAccessBeforeRetry = true;
            }
            catch
            {
                return false;
            }

            if (attempt == EvidenceCleanupMaxAttempts)
            {
                return false;
            }

            if (normalizeAccessBeforeRetry)
            {
                try
                {
                    normalizeAccess();
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Keep the retry bounded. A later delete may still succeed
                    // when the local lock or policy race was transient.
                }
                catch
                {
                    return false;
                }
            }

            await Task.Delay(EvidenceCleanupRetryDelay, cancellationToken);
        }

        return false;
    }

    private static bool IsRollbackStageFailure(string code) =>
        code is
            SetupErrorCodes.RollbackStateMismatch or
            SetupErrorCodes.RollbackServiceStopFailed or
            SetupErrorCodes.RollbackFileRestoreFailed or
            SetupErrorCodes.RollbackDataCleanupFailed or
            SetupErrorCodes.RollbackServiceRestoreFailed or
            SetupErrorCodes.RollbackHttpsFirewallRestoreFailed or
            SetupErrorCodes.RollbackLegacyFirewallRestoreFailed or
            SetupErrorCodes.RollbackJournalWriteFailed or
            SetupErrorCodes.RollbackEvidenceCleanupFailed or
            SetupErrorCodes.RollbackStagingCleanupFailed or
            SetupErrorCodes.RollbackBackupCleanupFailed or
            SetupErrorCodes.RollbackFailedDirectoryCleanupFailed or
            SetupErrorCodes.RollbackJournalCleanupFailed;

    private sealed record RollbackOutcome(
        bool Succeeded,
        IReadOnlyList<string> FailureCodes)
    {
        public static RollbackOutcome Success { get; } = new(true, []);

        public static RollbackOutcome Failed(IReadOnlyList<string> failureCodes) =>
            new(false, failureCodes.ToArray());
    }

    private async Task RecoverPendingTransactionAsync(
        DeploymentJournalStore journalStore,
        SetupStepRecorder steps,
        CancellationToken cancellationToken)
    {
        var pending = journalStore.Read();
        ValidatePendingTransaction(pending);

        var currentService = serviceManager.Capture(SetupConstants.ServiceName);
        fileSystem.ValidateRecoveryPaths(
            paths,
            currentService,
            pending.PreviousService,
            pending.DataDirectoryCreated &&
            !pending.DataDirectoryExistedBefore,
            [pending.StagingDirectory, pending.BackupDirectory, pending.FailedDirectory]);

        if (string.Equals(pending.Stage, RollbackCompletedStage, StringComparison.Ordinal))
        {
            await RecoverCompletedRollbackAsync(
                journalStore,
                pending,
                steps,
                cancellationToken);
            return;
        }

        if (string.Equals(pending.Stage, "committed", StringComparison.Ordinal))
        {
            await RecoverCommittedTransactionAsync(
                journalStore,
                pending,
                steps,
                cancellationToken);
            return;
        }

        pending = UpgradePendingJournalForRecovery(
            journalStore,
            pending,
            steps);
        var primaryCode = pending.PrimaryFailureCode ??
                          SetupErrorCodes.RecoveryRequired;
        var primaryMessage = pending.PrimaryFailureMessage ??
                             "이전 설치 작업이 완료되지 않았습니다.";
        var rollback = await TryRollbackAsync(
            journalStore,
            pending,
            pending.PreviousService,
            pending.PreviousHttpsFirewall,
            pending.PreviousHttpFirewall,
            pending.StagingDirectory,
            pending.BackupDirectory,
            pending.FailedDirectory,
            pending.InstallMovedToBackup,
            pending.StagingActivated,
            pending.DataDirectoryExistedBefore,
            pending.DataDirectoryCreated,
            pending.MutationStarted,
            pending.MutationStarted &&
            !FirewallSnapshotsAreUnavailable(
                pending.PreviousHttpsFirewall,
                pending.PreviousHttpFirewall),
            primaryCode,
            primaryMessage,
            steps,
            cancellationToken);
        if (!rollback.Succeeded)
        {
            throw new SetupException(
                SetupErrorCodes.RollbackFailed,
                "이전 설치 상태를 완전히 복구하지 못했습니다. 작업 기록과 백업을 보존했습니다.");
        }
    }

    private async Task RecoverCompletedRollbackAsync(
        DeploymentJournalStore journalStore,
        DeploymentJournal pending,
        SetupStepRecorder steps,
        CancellationToken cancellationToken)
    {
        var installExists = fileSystem.DirectoryExists(paths.InstallDirectory);
        var backupExists = fileSystem.DirectoryExists(pending.BackupDirectory);
        var stagingExists = fileSystem.DirectoryExists(pending.StagingDirectory);
        var failedExists = fileSystem.DirectoryExists(pending.FailedDirectory);
        var freshDataStillExists =
            pending.DataDirectoryCreated &&
            !pending.DataDirectoryExistedBefore &&
            fileSystem.DirectoryExists(paths.DataDirectory);
        var unexpectedFreshDataExists =
            !pending.DataDirectoryCreated &&
            !pending.DataDirectoryExistedBefore &&
            fileSystem.DirectoryExists(paths.DataDirectory);
        var missingPreexistingData =
            pending.DataDirectoryExistedBefore &&
            !fileSystem.DirectoryExists(paths.DataDirectory);
        var installStateIsSafe =
            pending.InstallMovedToBackup
                ? installExists
                : !pending.StagingActivated || !installExists;
        var transactionRemnantsAreSafe =
            pending.MutationStarted &&
            pending.StagingActivated &&
            !(stagingExists && failedExists);

        if (!installStateIsSafe ||
            backupExists ||
            freshDataStillExists ||
            unexpectedFreshDataExists ||
            missingPreexistingData ||
            !transactionRemnantsAreSafe)
        {
            throw new SetupException(
                SetupErrorCodes.RecoveryRequired,
                "완료된 이전 설치 복구 상태가 안전한 파일 시스템 조건과 일치하지 않습니다.");
        }

        var cleanupFailed = false;
        if (!await TryDeleteEvidenceDirectoryAsync(
                pending.StagingDirectory,
                cancellationToken))
        {
            cleanupFailed = true;
            RecordPendingEvidenceCleanupFailure(
                journalStore,
                pending,
                SetupErrorCodes.RollbackStagingCleanupFailed,
                "임시 설치 자료 정리",
                "완료된 이전 설치의 임시 자료를 정리하지 못했습니다.",
                steps);
        }

        if (!await TryDeleteEvidenceDirectoryAsync(
                pending.BackupDirectory,
                cancellationToken))
        {
            cleanupFailed = true;
            RecordPendingEvidenceCleanupFailure(
                journalStore,
                pending,
                SetupErrorCodes.RollbackBackupCleanupFailed,
                "이전 파일 정리",
                "완료된 이전 설치의 백업 자료를 정리하지 못했습니다.",
                steps);
        }

        if (!await TryDeleteEvidenceDirectoryAsync(
                pending.FailedDirectory,
                cancellationToken))
        {
            cleanupFailed = true;
            RecordPendingEvidenceCleanupFailure(
                journalStore,
                pending,
                SetupErrorCodes.RollbackFailedDirectoryCleanupFailed,
                "실패 설치 자료 정리",
                "완료된 이전 설치의 실패 자료를 정리하지 못했습니다.",
                steps);
        }

        if (cleanupFailed)
        {
            throw new SetupException(
                SetupErrorCodes.RollbackFailed,
                "완료된 이전 설치 복구 자료를 정리할 수 없습니다. 다음 실행에서 다시 시도합니다.");
        }

        if (!await TryDeleteJournalAsync(journalStore, cancellationToken))
        {
            RecordPendingEvidenceCleanupFailure(
                journalStore,
                pending,
                SetupErrorCodes.RollbackJournalCleanupFailed,
                "복구 기록 정리",
                "완료된 이전 설치 복구 기록을 정리하지 못했습니다.",
                steps);
            throw new SetupException(
                SetupErrorCodes.RollbackFailed,
                "완료된 이전 설치 복구 기록을 정리할 수 없습니다. 다음 실행에서 다시 시도합니다.");
        }

        steps.Add(new SetupStepResult(
            "ROLLBACK_RECOVERY_CLEANED",
            "이전 복구 정리",
            SetupStepState.Information,
            "완료된 이전 설치 복구 자료를 안전하게 정리했습니다."));
    }

    private async Task RecoverCommittedTransactionAsync(
        DeploymentJournalStore journalStore,
        DeploymentJournal pending,
        SetupStepRecorder steps,
        CancellationToken cancellationToken)
    {
        if (!fileSystem.DirectoryExists(paths.InstallDirectory) ||
            !fileSystem.DirectoryExists(paths.DataDirectory))
        {
            throw new SetupException(
                SetupErrorCodes.RecoveryRequired,
                "완료된 이전 설치 기록과 실제 Agent 프로그램 또는 데이터 폴더가 일치하지 않습니다.");
        }

        var cleanupFailed = false;
        foreach (var target in new[]
                 {
                     (
                         Path: pending.StagingDirectory,
                         Code: SetupErrorCodes.RollbackStagingCleanupFailed,
                         Label: "임시 설치 자료 정리",
                         Message: "완료된 설치의 임시 자료를 정리하지 못했습니다."),
                     (
                         Path: pending.BackupDirectory,
                         Code: SetupErrorCodes.RollbackBackupCleanupFailed,
                         Label: "이전 파일 정리",
                         Message: "완료된 설치의 백업 자료를 정리하지 못했습니다."),
                     (
                         Path: pending.FailedDirectory,
                         Code: SetupErrorCodes.RollbackFailedDirectoryCleanupFailed,
                         Label: "실패 설치 자료 정리",
                         Message: "완료된 설치의 실패 자료를 정리하지 못했습니다.")
                 })
        {
            if (!await TryDeleteEvidenceDirectoryAsync(
                    target.Path,
                    cancellationToken))
            {
                // A committed installation remains authoritative. Preserve leftovers.
                cleanupFailed = true;
                RecordPendingEvidenceCleanupFailure(
                    journalStore,
                    pending,
                    target.Code,
                    target.Label,
                    target.Message,
                    steps);
            }
        }

        if (cleanupFailed)
        {
            throw new SetupException(
                SetupErrorCodes.RollbackFailed,
                "완료된 이전 설치 자료를 정리할 수 없습니다. 다음 실행에서 다시 시도합니다.");
        }

        if (!await TryDeleteJournalAsync(journalStore, cancellationToken))
        {
            RecordPendingEvidenceCleanupFailure(
                journalStore,
                pending,
                SetupErrorCodes.RollbackJournalCleanupFailed,
                "작업 기록 정리",
                "완료된 이전 설치 작업 기록을 정리하지 못했습니다.",
                steps);
            throw new SetupException(
                SetupErrorCodes.RollbackFailed,
                "완료된 이전 설치 작업 기록을 정리할 수 없습니다. 다음 실행에서 다시 시도합니다.");
        }

        steps.Add(new SetupStepResult(
            "COMMITTED_TRANSACTION_CLEANED",
            "이전 작업 정리",
            SetupStepState.Information,
            "완료된 이전 설치 작업 기록을 정리했습니다."));
    }

    private void ValidateExistingServiceContract(ServiceSnapshot service)
    {
        if (!service.Exists)
        {
            return;
        }

        var expectedBinary = $"\"{paths.AgentExecutablePath}\" --service";
        var validAccount = string.Equals(
            service.AccountName,
            $@"NT SERVICE\{SetupConstants.ServiceName}",
            StringComparison.OrdinalIgnoreCase) ||
            ServiceAccountContract.AllowsLegacyLocalServiceDataOwner(service);
        if (!string.Equals(service.BinaryPath, expectedBinary, StringComparison.Ordinal) ||
            service.StartType != 2 ||
            !validAccount)
        {
            throw new SetupException(
                SetupErrorCodes.ServiceFailed,
                "같은 이름의 Windows 서비스가 Agent 설치 계약과 일치하지 않아 안전을 위해 중단했습니다.");
        }
    }

    private void ValidatePendingTransaction(DeploymentJournal pending)
    {
        var expectedStaging =
            $"{paths.InstallDirectory}.__staging_{pending.TransactionId}";
        var expectedBackup =
            $"{paths.InstallDirectory}.__backup_{pending.TransactionId}";
        var expectedFailed =
            $"{paths.InstallDirectory}.__failed_{pending.TransactionId}";
        if (pending.TransactionId.Length != 32 ||
            pending.TransactionId.Any(character => !Uri.IsHexDigit(character)) ||
            !PathsEqual(pending.StagingDirectory, expectedStaging) ||
            !PathsEqual(pending.BackupDirectory, expectedBackup) ||
            !PathsEqual(pending.FailedDirectory, expectedFailed))
        {
            throw new SetupException(
                SetupErrorCodes.RecoveryRequired,
                "이전 설치 기록의 경로가 현재 Agent 제품 경로와 일치하지 않습니다.");
        }

        try
        {
            ValidateExistingServiceContract(pending.PreviousService);
        }
        catch (SetupException exception)
        {
            throw new SetupException(
                SetupErrorCodes.RecoveryRequired,
                "이전 설치 기록의 서비스 상태가 Agent 복구 계약과 일치하지 않습니다.",
                exception);
        }

        if (!string.Equals(
                pending.PreviousHttpsFirewall.Name,
                SetupConstants.FirewallRuleName,
                StringComparison.Ordinal) ||
            !string.Equals(
                pending.PreviousHttpFirewall.Name,
                SetupConstants.LegacyFirewallRuleName,
                StringComparison.Ordinal))
        {
            throw new SetupException(
                SetupErrorCodes.RecoveryRequired,
                "이전 설치 기록의 방화벽 상태가 Agent 복구 계약과 일치하지 않습니다.");
        }

        ValidatePendingJournalState(pending);
        ValidatePendingRecoveryTopology(pending);
    }

    private void ValidatePendingRecoveryTopology(DeploymentJournal pending)
    {
        var installExists = fileSystem.DirectoryExists(paths.InstallDirectory);
        var backupExists = fileSystem.DirectoryExists(pending.BackupDirectory);
        var stagingExists = fileSystem.DirectoryExists(pending.StagingDirectory);
        var failedExists = fileSystem.DirectoryExists(pending.FailedDirectory);
        var dataExists = fileSystem.DirectoryExists(paths.DataDirectory);

        if (string.Equals(
                pending.Stage,
                RollbackCompletedStage,
                StringComparison.Ordinal))
        {
            var installStateIsSafe =
                pending.InstallMovedToBackup
                    ? installExists
                    : !pending.StagingActivated || !installExists;
            var transactionRemnantsAreSafe =
                pending.MutationStarted &&
                pending.StagingActivated &&
                !(stagingExists && failedExists);
            var freshDataStillExists =
                pending.DataDirectoryCreated &&
                !pending.DataDirectoryExistedBefore &&
                dataExists;
            var unexpectedFreshDataExists =
                !pending.DataDirectoryCreated &&
                !pending.DataDirectoryExistedBefore &&
                dataExists;
            var missingPreexistingData =
                pending.DataDirectoryExistedBefore &&
                !dataExists;
            if (!installStateIsSafe ||
                backupExists ||
                freshDataStillExists ||
                unexpectedFreshDataExists ||
                missingPreexistingData ||
                !transactionRemnantsAreSafe)
            {
                throw new SetupException(
                    SetupErrorCodes.RollbackStateMismatch,
                    "완료된 복구 기록과 현재 파일 상태가 일치하지 않습니다.");
            }

            return;
        }

        if (string.Equals(pending.Stage, "committed", StringComparison.Ordinal))
        {
            if (!installExists || !dataExists)
            {
                throw new SetupException(
                    SetupErrorCodes.RollbackStateMismatch,
                    "완료된 설치 기록과 현재 Agent 파일 상태가 일치하지 않습니다.");
            }

            return;
        }

        if ((!pending.InstallMovedToBackup && backupExists) ||
            (!pending.StagingActivated && failedExists) ||
            (stagingExists && failedExists) ||
            (pending.DataDirectoryExistedBefore && !dataExists) ||
            (!pending.DataDirectoryExistedBefore &&
             !pending.DataDirectoryCreated &&
             dataExists) ||
            (pending.InstallMovedToBackup &&
             !pending.StagingActivated &&
             installExists &&
             backupExists))
        {
            throw new SetupException(
                SetupErrorCodes.RollbackStateMismatch,
                "이전 설치 기록과 현재 파일 상태가 일치하지 않습니다.");
        }
    }

    private PendingRecoveryInspection BuildPendingInspection(
        DeploymentJournal pending,
        ServiceSnapshot currentService,
        bool canRecover,
        string code,
        string message) =>
        new(true, canRecover, code, message)
        {
            JournalFormatVersion = pending.FormatVersion,
            JournalStage = pending.Stage,
            PrimaryFailureCode = pending.PrimaryFailureCode,
            PrimaryFailureMessage = pending.PrimaryFailureMessage,
            RollbackFailureCodes = pending.RollbackFailureCodes,
            AgentHealthCode = pending.AgentHealthCode,
            AgentRestartObserved = pending.AgentRestartObserved,
            AgentServiceRunningObserved =
                pending.AgentServiceRunningObserved,
            AgentListenerOwnedObserved =
                pending.AgentListenerOwnedObserved,
            AgentHttpAttemptCount = pending.AgentHttpAttemptCount,
            AgentLastTransportPhase = pending.AgentLastTransportPhase,
            ServiceState = GetServiceState(currentService),
            InstallDirectoryExists =
                fileSystem.DirectoryExists(paths.InstallDirectory),
            StagingDirectoryExists =
                fileSystem.DirectoryExists(pending.StagingDirectory),
            BackupDirectoryExists =
                fileSystem.DirectoryExists(pending.BackupDirectory),
            FailedDirectoryExists =
                fileSystem.DirectoryExists(pending.FailedDirectory),
            DataDirectoryExists =
                fileSystem.DirectoryExists(paths.DataDirectory)
        };

    private PendingRecoveryInspection BuildUnsafePendingInspection(
        DeploymentJournalStore journalStore,
        string code,
        string message)
    {
        DeploymentJournal? pending = null;
        try
        {
            pending = journalStore.Read();
        }
        catch
        {
            // The caller already classified the journal as unsafe. Do not use
            // untrusted transaction paths merely to enrich diagnostics.
        }

        ServiceSnapshot? service = null;
        try
        {
            service = serviceManager.Capture(SetupConstants.ServiceName);
        }
        catch
        {
            // Keep a safe "unknown" state.
        }

        return new PendingRecoveryInspection(true, false, code, message)
        {
            JournalFormatVersion = pending?.FormatVersion,
            JournalStage = pending?.Stage,
            PrimaryFailureCode = pending?.PrimaryFailureCode,
            PrimaryFailureMessage = pending?.PrimaryFailureMessage,
            RollbackFailureCodes = pending?.RollbackFailureCodes ?? [],
            AgentHealthCode = pending?.AgentHealthCode,
            AgentRestartObserved = pending?.AgentRestartObserved ?? false,
            AgentServiceRunningObserved =
                pending?.AgentServiceRunningObserved ?? false,
            AgentListenerOwnedObserved =
                pending?.AgentListenerOwnedObserved ?? false,
            AgentHttpAttemptCount = pending?.AgentHttpAttemptCount ?? 0,
            AgentLastTransportPhase =
                pending?.AgentLastTransportPhase ??
                AgentHealthTransportPhase.NotStarted,
            ServiceState = service is null
                ? "unknown"
                : GetServiceState(service),
            EvidenceStateKnown = false,
            InstallDirectoryExists =
                DirectoryExistsSafe(paths.InstallDirectory),
            DataDirectoryExists =
                DirectoryExistsSafe(paths.DataDirectory)
        };
    }

    private bool DirectoryExistsSafe(string path)
    {
        try
        {
            return fileSystem.DirectoryExists(path);
        }
        catch
        {
            return false;
        }
    }

    private static string GetServiceState(ServiceSnapshot service) =>
        !service.Exists
            ? "missing"
            : service.Running
                ? "running"
                : "stopped";

    internal static string AgentHealthDecisionCode(AgentHealthProbeCode code) =>
        $"AGENT_HEALTH_{code switch
        {
            AgentHealthProbeCode.Ready => "READY",
            AgentHealthProbeCode.ServiceUnavailable => "SERVICE_UNAVAILABLE",
            AgentHealthProbeCode.ServiceInspectionFailed => "SERVICE_INSPECTION_FAILED",
            AgentHealthProbeCode.TcpNotListening => "TCP_NOT_LISTENING",
            AgentHealthProbeCode.TcpOwnedByOtherProcess => "TCP_FOREIGN_OWNER",
            AgentHealthProbeCode.TcpOwnershipQueryFailed => "TCP_QUERY_FAILED",
            AgentHealthProbeCode.HttpsRequestFailed => "HTTPS_REQUEST_FAILED",
            AgentHealthProbeCode.HttpStatusInvalid => "HTTP_STATUS_INVALID",
            AgentHealthProbeCode.PayloadTooLarge => "PAYLOAD_TOO_LARGE",
            AgentHealthProbeCode.PayloadInvalid => "PAYLOAD_INVALID",
            AgentHealthProbeCode.ApiVersionMismatch => "API_VERSION_MISMATCH",
            AgentHealthProbeCode.ProtocolMismatch => "PROTOCOL_MISMATCH",
            AgentHealthProbeCode.ProductVersionMismatch => "PRODUCT_VERSION_MISMATCH",
            AgentHealthProbeCode.HttpsTlsFailed => "HTTPS_TLS_FAILED",
            AgentHealthProbeCode.HttpsRequestTimeout => "HTTPS_REQUEST_TIMEOUT",
            AgentHealthProbeCode.HttpsConnectionReset => "HTTPS_CONNECTION_RESET",
            AgentHealthProbeCode.HttpsEof => "HTTPS_EOF",
            AgentHealthProbeCode.HttpsConnectFailed => "HTTPS_CONNECT_FAILED",
            _ => "DEADLINE_EXCEEDED"
        }}";

    internal static string AgentHealthDisplayName(AgentHealthProbeCode code) =>
        code switch
        {
            AgentHealthProbeCode.Ready => "준비 완료",
            AgentHealthProbeCode.ServiceUnavailable => "서비스 실행 상태",
            AgentHealthProbeCode.ServiceInspectionFailed => "서비스 상태 확인",
            AgentHealthProbeCode.TcpNotListening => "로컬 TCP/18443 수신",
            AgentHealthProbeCode.TcpOwnedByOtherProcess => "TCP/18443 소유 프로세스",
            AgentHealthProbeCode.TcpOwnershipQueryFailed => "TCP 수신 상태 확인",
            AgentHealthProbeCode.HttpsRequestFailed => "로컬 HTTPS 응답",
            AgentHealthProbeCode.HttpStatusInvalid => "HTTP 상태 코드",
            AgentHealthProbeCode.PayloadTooLarge => "준비 응답 크기",
            AgentHealthProbeCode.PayloadInvalid => "준비 응답 형식",
            AgentHealthProbeCode.ApiVersionMismatch => "Agent API 버전",
            AgentHealthProbeCode.ProtocolMismatch => "Agent 통신 프로토콜",
            AgentHealthProbeCode.ProductVersionMismatch => "Agent 제품 버전",
            AgentHealthProbeCode.HttpsTlsFailed => "로컬 HTTPS TLS 협상",
            AgentHealthProbeCode.HttpsRequestTimeout => "로컬 HTTPS 응답 제한 시간",
            AgentHealthProbeCode.HttpsConnectionReset => "로컬 HTTPS 연결 재설정",
            AgentHealthProbeCode.HttpsEof => "로컬 HTTPS 응답 조기 종료",
            AgentHealthProbeCode.HttpsConnectFailed => "로컬 HTTPS 연결",
            _ => "준비 확인 제한 시간"
        };

    internal static string AgentHealthFailureMessage(
        AgentHealthProbeResult health) =>
        "Agent PC 내부 통신 실패: Setup → 127.0.0.1:18443 → Agent 서비스 구간에서 " +
        "로컬 HTTPS 응답을 확인하지 못했습니다. Viewer IP나 스위치 관리망 설정 문제는 아닙니다. " +
        $"진단 단계: {AgentHealthDisplayName(health.Code)} / " +
        $"HTTPS 진행: {AgentHealthTransportPhaseDisplayName(health.LastTransportPhase)}.";

    internal static string AgentHealthTransportPhaseDisplayName(
        AgentHealthTransportPhase phase) =>
        phase switch
        {
            AgentHealthTransportPhase.ListenerOwned => "TCP/18443 Agent 소유 확인",
            AgentHealthTransportPhase.RequestStarted => "HTTPS 요청 시작",
            AgentHealthTransportPhase.ResponseHeaders => "HTTPS 응답 헤더",
            AgentHealthTransportPhase.ResponseBody => "HTTPS 응답 본문",
            AgentHealthTransportPhase.ReadinessValidated => "준비 응답 검증",
            _ => "HTTPS 요청 전"
        };

    private static void ValidatePendingJournalState(DeploymentJournal pending)
    {
        var dataFlagsAreValid =
            !pending.DataDirectoryCreated ||
            !pending.DataDirectoryExistedBefore;
        var stageRequiresDataDecision = pending.Stage is
            "service-configured" or
            "firewall-configured" or
            "service-started" or
            "committed";
        var dataDecisionIsValid =
            !stageRequiresDataDecision ||
            pending.DataDirectoryExistedBefore != pending.DataDirectoryCreated;
        var stageFlagsAreValid = pending.Stage switch
        {
            "prepared" =>
                !pending.MutationStarted &&
                !pending.InstallMovedToBackup &&
                !pending.StagingActivated &&
                !pending.DataDirectoryCreated,
            "service-stop-pending" =>
                pending.MutationStarted &&
                !pending.InstallMovedToBackup &&
                !pending.StagingActivated &&
                !pending.DataDirectoryCreated,
            "backup-move-pending" =>
                pending.MutationStarted &&
                pending.InstallMovedToBackup &&
                !pending.StagingActivated &&
                !pending.DataDirectoryCreated,
            "activation-pending" =>
                pending.MutationStarted &&
                pending.StagingActivated &&
                !pending.DataDirectoryCreated,
            "data-directory-create-pending" =>
                pending.MutationStarted &&
                pending.StagingActivated &&
                pending.DataDirectoryCreated,
            "service-configured" or
            "firewall-configured" or
            "service-started" or
            "committed" or
            RollbackCompletedStage =>
                pending.MutationStarted &&
                pending.StagingActivated,
            _ => false
        };
        var formatAndStageAreCompatible =
            pending.FormatVersion == DeploymentJournalStore.CurrentFormatVersion ||
            !string.Equals(
                pending.Stage,
                RollbackCompletedStage,
                StringComparison.Ordinal);
        if (!dataFlagsAreValid ||
            !dataDecisionIsValid ||
            !stageFlagsAreValid ||
            !formatAndStageAreCompatible)
        {
            throw new SetupException(
                SetupErrorCodes.RecoveryRequired,
                "이전 설치 기록의 단계와 상태 플래그가 일치하지 않습니다.");
        }
    }

    private static DeploymentJournal UpgradePendingJournalForRecovery(
        DeploymentJournalStore journalStore,
        DeploymentJournal pending,
        SetupStepRecorder steps)
    {
        if (pending.FormatVersion == DeploymentJournalStore.CurrentFormatVersion)
        {
            return pending;
        }

        var upgraded = pending with
        {
            FormatVersion = DeploymentJournalStore.CurrentFormatVersion
        };
        try
        {
            journalStore.Write(upgraded);
            return upgraded;
        }
        catch (Exception exception)
        {
            steps.Add(Failed(
                SetupErrorCodes.RollbackJournalWriteFailed,
                "복구 기록 전환",
                "이전 설치 작업 기록을 안전한 복구 형식으로 저장하지 못했습니다."));
            throw new SetupException(
                SetupErrorCodes.RollbackFailed,
                "이전 설치 작업 기록을 안전한 복구 형식으로 전환할 수 없습니다. 기존 기록을 보존했습니다.",
                exception);
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static SetupStepResult Succeeded(string code, string label, string message) =>
        new(code, label, SetupStepState.Succeeded, message);

    private static SetupStepResult Failed(string code, string label, string message) =>
        new(code, label, SetupStepState.Failed, message);

    private static bool IsAgentRuntimeFile(PackageFile file)
    {
        if (string.Equals(
                file.Name,
                SetupConstants.AgentExecutableName,
                StringComparison.Ordinal))
        {
            return true;
        }

        var extension = Path.GetExtension(file.Name);
        return extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
               file.Name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase) ||
               file.Name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase);
    }

    private void VerifyStagedRuntime(AgentPackage package, string stagingDirectory)
    {
        foreach (var runtimeFile in package.VerifiedFiles.Where(IsAgentRuntimeFile))
        {
            var stagedRuntimePath = Path.Combine(stagingDirectory, runtimeFile.Name);
            if (!fileSystem.FileExists(stagedRuntimePath) ||
                !string.Equals(
                    fileSystem.ComputeSha256(stagedRuntimePath),
                    runtimeFile.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new SetupException(
                    SetupErrorCodes.PackageHashMismatch,
                    $"보호된 임시 폴더의 필수 파일 무결성 확인에 실패했습니다: {runtimeFile.Name}");
            }
        }
    }
}
