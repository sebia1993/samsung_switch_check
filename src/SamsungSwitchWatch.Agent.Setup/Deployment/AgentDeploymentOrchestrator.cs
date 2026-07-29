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
    private const int FirewallVerificationRetryCount = 10;
    private static readonly TimeSpan FirewallVerificationRetryDelay =
        TimeSpan.FromMilliseconds(200);

    public async Task<SetupOperationResult> DeployAsync(
        SetupRequest request,
        CancellationToken cancellationToken)
    {
        var steps = new List<SetupStepResult>();
        var processGateEntered = false;
        IDisposable? machineLease = null;
        try
        {
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
        List<SetupStepResult> steps,
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
        var journalStore = new DeploymentJournalStore(fileSystem, paths);
        DeploymentJournal? journal = null;

        try
        {
            if (!administratorChecker.IsAdministrator())
            {
                throw new SetupException(
                    SetupErrorCodes.AdministratorRequired,
                    "Agent 서비스 설치에는 관리자 권한이 필요합니다.");
            }

            if (journalStore.Exists)
            {
                RecoverPendingTransaction(journalStore, steps);
            }

            SetupDiagnosticsService.ValidateInput(request);
            var package = packageValidator.Validate(paths.PackageDirectory);
            steps.Add(Succeeded(
                "PACKAGE_VALID",
                "패키지 확인",
                $"Agent {package.Version} 파일 무결성이 정상입니다."));

            var transactionId = Guid.NewGuid().ToString("N");
            stagingDirectory = $"{paths.InstallDirectory}.__staging_{transactionId}";
            backupDirectory = $"{paths.InstallDirectory}.__backup_{transactionId}";
            failedDirectory = $"{paths.InstallDirectory}.__failed_{transactionId}";

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

            previousHttpsFirewall = firewallManager.Capture(SetupConstants.FirewallRuleName);
            previousHttpFirewall = firewallManager.Capture(SetupConstants.LegacyFirewallRuleName);
            var firewallAssessment = firewallManager.AssertSecurityGate(
                SetupConstants.HttpsPort,
                paths.AgentExecutablePath);
            SetupDiagnosticsService.AddFirewallWarnings(
                steps,
                firewallAssessment);

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

            var existingConfiguration = fileSystem.FileExists(paths.ProductionConfigurationPath)
                ? fileSystem.ReadAllText(paths.ProductionConfigurationPath)
                : null;
            var configuration = AgentConfigurationFactory.Create(
                paths.DataDirectory,
                request.TargetCidrs,
                request.ViewerIpv4,
                existingConfiguration);

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
            journal = journal with
            {
                Stage = "service-stop-pending",
                MutationStarted = true
            };
            journalStore.Write(journal);
            if (previousService.Exists && previousService.Running)
            {
                serviceManager.Stop(SetupConstants.ServiceName, TimeSpan.FromSeconds(20));
            }

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

            var serviceBinaryPath = $"\"{paths.AgentExecutablePath}\" --service";
            serviceManager.InstallOrUpdate(
                SetupConstants.ServiceName,
                SetupConstants.ServiceDisplayName,
                serviceBinaryPath,
                $@"NT SERVICE\{SetupConstants.ServiceName}");
            serviceManager.ConfigureRecovery(SetupConstants.ServiceName);
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
                throw new SetupException(
                    SetupErrorCodes.FirewallFailed,
                    "Viewer 전용 방화벽 규칙을 확인하지 못했습니다. " +
                    $"({firewallVerification.MismatchCode})");
            }
            _ = firewallManager.AssertSecurityGate(
                SetupConstants.HttpsPort,
                paths.AgentExecutablePath);
            journal = journal with { Stage = "firewall-configured" };
            journalStore.Write(journal);

            steps.Add(Succeeded(
                "FIREWALL_CONFIGURED",
                "방화벽 구성",
                $"제품 소유 Viewer {request.ViewerIpv4}/32 HTTPS/18443 규칙을 구성했고 Agent 원격 업무 API도 동일한 Viewer IP만 허용합니다."));

            serviceManager.Start(SetupConstants.ServiceName, TimeSpan.FromSeconds(30));
            journal = journal with { Stage = "service-started" };
            journalStore.Write(journal);
            var startedService = serviceManager.Capture(SetupConstants.ServiceName);
            var ready = await healthProbe.WaitUntilReadyAsync(
                new Uri("https://127.0.0.1:18443/health/ready"),
                package.Version,
                startedService.ProcessId,
                TimeSpan.FromSeconds(60),
                cancellationToken);
            if (!ready)
            {
                throw new SetupException(
                    SetupErrorCodes.HealthFailed,
                    "Agent 서비스가 제한 시간 안에 준비 상태가 되지 않았습니다.");
            }

            steps.Add(Succeeded(
                "AGENT_READY",
                "Agent 확인",
                "Agent 서비스가 정상적으로 실행되고 있습니다."));

            // Health success is the transaction commit boundary. Backup cleanup
            // must never turn a working installation into a rollback attempt
            // because recursive deletion may already be partially complete.
            journal = journal with { Stage = "committed" };
            journalStore.Write(journal);
            if (installMovedToBackup && fileSystem.DirectoryExists(backupDirectory))
            {
                try
                {
                    fileSystem.DeleteDirectory(backupDirectory, recursive: true);
                }
                catch
                {
                    steps.Add(new SetupStepResult(
                        "BACKUP_CLEANUP_PENDING",
                        "이전 파일 정리",
                        SetupStepState.Information,
                        "설치는 완료됐지만 이전 버전 백업 일부가 남았습니다. Agent 동작에는 영향이 없습니다."));
                }
            }

            try
            {
                journalStore.Delete();
            }
            catch
            {
                steps.Add(new SetupStepResult(
                    "JOURNAL_CLEANUP_PENDING",
                    "작업 기록 정리",
                    SetupStepState.Information,
                    "설치는 완료됐지만 완료된 작업 기록이 남았습니다. 다음 실행에서 안전하게 정리합니다."));
            }

            return SetupOperationResult.Success(
                "Agent 설치 또는 업데이트가 완료되었습니다.",
                steps);
        }
        catch (OperationCanceledException)
        {
            var rollbackCode = currentTransactionOwned
                ? TryRollback(
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
                    steps)
                : null;
            if (currentTransactionOwned)
            {
                DeleteJournalAfterRollback(journalStore, journal, rollbackCode);
            }
            var code = rollbackCode ?? SetupErrorCodes.Cancelled;
            var message = rollbackCode is null
                ? "설치가 취소되어 이전 상태로 복구했습니다."
                : "설치 취소 후 이전 상태를 완전히 복구하지 못했습니다. 관리자 확인이 필요합니다.";
            steps.Add(Failed(code, "설치 취소", message));
            return SetupOperationResult.Failure(code, message, steps);
        }
        catch (SetupException exception)
        {
            var rollbackCode = currentTransactionOwned
                ? TryRollback(
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
                    steps)
                : null;
            if (currentTransactionOwned)
            {
                DeleteJournalAfterRollback(journalStore, journal, rollbackCode);
            }
            var code = rollbackCode ?? exception.Code;
            var message = rollbackCode is null
                ? exception.Message
                : "설치에 실패했고 이전 상태를 완전히 복구하지 못했습니다. 관리자 확인이 필요합니다.";
            steps.Add(Failed(code, "설치 실패", message));
            return SetupOperationResult.Failure(code, message, steps);
        }
        catch (Exception)
        {
            var rollbackCode = currentTransactionOwned
                ? TryRollback(
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
                    steps)
                : null;
            if (currentTransactionOwned)
            {
                DeleteJournalAfterRollback(journalStore, journal, rollbackCode);
            }
            var code = rollbackCode ?? SetupErrorCodes.Unexpected;
            var message = rollbackCode is null
                ? "예상하지 못한 오류로 설치를 완료하지 못했습니다."
                : "설치에 실패했고 이전 상태를 완전히 복구하지 못했습니다. 관리자 확인이 필요합니다.";
            steps.Add(Failed(code, "설치 실패", message));
            return SetupOperationResult.Failure(code, message, steps);
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

    private string? TryRollback(
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
        List<SetupStepResult> steps)
    {
        var failed = false;
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
            failed = true;
        }

        if (failed)
        {
            steps.Add(new SetupStepResult(
                SetupErrorCodes.RollbackFailed,
                "자동 복구",
                SetupStepState.Failed,
                "자동 복구를 시작하기 전 파일 상태가 작업 기록과 일치하지 않습니다. 설치 폴더와 백업 자료를 보존했습니다."));
            return SetupErrorCodes.RollbackFailed;
        }

        if (mutationStarted)
        {
            try
            {
                var current = serviceManager.Capture(SetupConstants.ServiceName);
                if (current.Exists && current.Running)
                {
                    serviceManager.Stop(SetupConstants.ServiceName, TimeSpan.FromSeconds(20));
                }
            }
            catch
            {
                failed = true;
            }
        }

        try
        {
            // A previous rollback may have moved backup -> install and then
            // failed while restoring the install ACL. In that state the failed
            // new version and restored old version both exist, so repeating the
            // install -> failed move would collide and strand recovery.
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
                if (failedDirectory is null)
                {
                    throw new InvalidOperationException();
                }
                if (failedExists)
                {
                    throw new InvalidOperationException();
                }

                fileSystem.MoveDirectory(paths.InstallDirectory, failedDirectory);
                installExists = false;
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

                    fileSystem.MoveDirectory(backupDirectory, paths.InstallDirectory);
                    installExists = true;
                }
                if (!installExists)
                {
                    throw new InvalidOperationException();
                }

                fileSystem.EnsureDirectoryAccess(
                    paths.InstallDirectory,
                    DirectoryAccessKind.ProgramReadExecute);
            }

            if (dataDirectoryCreated &&
                !dataDirectoryExistedBefore &&
                fileSystem.DirectoryExists(paths.DataDirectory))
            {
                var currentService = serviceManager.Capture(SetupConstants.ServiceName);
                fileSystem.ValidateRecoveryPaths(
                    paths,
                    currentService,
                    previousService ?? ServiceSnapshot.Missing,
                    allowFreshCreatedDataCleanup: true,
                    new[] { stagingDirectory, backupDirectory, failedDirectory }
                        .Where(path => path is not null)
                        .Select(path => path!)
                        .ToArray());
                fileSystem.DeleteDirectory(paths.DataDirectory, recursive: true);
            }
        }
        catch
        {
            failed = true;
        }

        if (mutationStarted)
        {
            try
            {
                if (previousService is not null)
                {
                    serviceManager.Restore(SetupConstants.ServiceName, previousService);
                }
            }
            catch
            {
                failed = true;
            }

            try
            {
                if (previousHttpsFirewall is not null)
                {
                    firewallManager.Restore(previousHttpsFirewall);
                }

                if (previousHttpFirewall is not null)
                {
                    firewallManager.Restore(previousHttpFirewall);
                }
            }
            catch
            {
                failed = true;
            }
        }

        if (!failed && stagingActivated && journal is not null)
        {
            try
            {
                journalStore.Write(journal with
                {
                    FormatVersion = DeploymentJournalStore.CurrentFormatVersion,
                    Stage = RollbackCompletedStage,
                    InstallMovedToBackup = installMovedToBackup,
                    StagingActivated = true,
                    DataDirectoryCreated = dataDirectoryCreated
                });
            }
            catch
            {
                // Keep the failed new installation as recovery evidence unless
                // the completed rollback marker is durably persisted first.
                failed = true;
            }
        }

        if (!failed)
        {
            try
            {
                if (stagingDirectory is not null &&
                    fileSystem.DirectoryExists(stagingDirectory))
                {
                    fileSystem.DeleteDirectory(stagingDirectory, recursive: true);
                }

                if (failedDirectory is not null && fileSystem.DirectoryExists(failedDirectory))
                {
                    fileSystem.DeleteDirectory(failedDirectory, recursive: true);
                }
            }
            catch
            {
                failed = true;
            }
        }

        steps.Add(new SetupStepResult(
            failed ? SetupErrorCodes.RollbackFailed : "ROLLBACK_COMPLETED",
            "자동 복구",
            failed ? SetupStepState.Failed : SetupStepState.Succeeded,
            failed
                ? "자동 복구 일부를 완료하지 못했습니다. 설치 폴더의 백업 자료를 보존했습니다."
                : "설치 전 프로그램·서비스·방화벽 상태로 복구했습니다."));

        return failed ? SetupErrorCodes.RollbackFailed : null;
    }

    private void RecoverPendingTransaction(
        DeploymentJournalStore journalStore,
        List<SetupStepResult> steps)
    {
        var pending = journalStore.Read();
        var expectedStaging = $"{paths.InstallDirectory}.__staging_{pending.TransactionId}";
        var expectedBackup = $"{paths.InstallDirectory}.__backup_{pending.TransactionId}";
        var expectedFailed = $"{paths.InstallDirectory}.__failed_{pending.TransactionId}";
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
        ValidatePendingJournalState(pending);

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
            RecoverCompletedRollback(journalStore, pending, steps);
            return;
        }

        if (string.Equals(pending.Stage, "committed", StringComparison.Ordinal))
        {
            RecoverCommittedTransaction(journalStore, pending, steps);
            return;
        }

        pending = UpgradePendingJournalForRecovery(journalStore, pending);
        var rollbackCode = TryRollback(
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
            steps);
        if (rollbackCode is not null)
        {
            throw new SetupException(
                SetupErrorCodes.RecoveryRequired,
                "이전 설치 상태를 자동 복구하지 못했습니다. 작업 기록과 백업을 보존했습니다.");
        }

        try
        {
            journalStore.Delete();
        }
        catch (Exception exception)
        {
            throw new SetupException(
                SetupErrorCodes.RecoveryRequired,
                "이전 설치 복구 기록을 정리할 수 없습니다. 다음 실행에서 복구를 다시 시도합니다.",
                exception);
        }
    }

    private void RecoverCompletedRollback(
        DeploymentJournalStore journalStore,
        DeploymentJournal pending,
        List<SetupStepResult> steps)
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

        try
        {
            if (stagingExists)
            {
                fileSystem.DeleteDirectory(pending.StagingDirectory, recursive: true);
            }

            if (failedExists)
            {
                fileSystem.DeleteDirectory(pending.FailedDirectory, recursive: true);
            }

            journalStore.Delete();
        }
        catch (Exception exception)
        {
            throw new SetupException(
                SetupErrorCodes.RecoveryRequired,
                "완료된 이전 설치 복구 자료를 정리할 수 없습니다. 다음 실행에서 다시 시도합니다.",
                exception);
        }

        steps.Add(new SetupStepResult(
            "ROLLBACK_RECOVERY_CLEANED",
            "이전 복구 정리",
            SetupStepState.Information,
            "완료된 이전 설치 복구 자료를 안전하게 정리했습니다."));
    }

    private void RecoverCommittedTransaction(
        DeploymentJournalStore journalStore,
        DeploymentJournal pending,
        List<SetupStepResult> steps)
    {
        if (!fileSystem.DirectoryExists(paths.InstallDirectory) ||
            !fileSystem.DirectoryExists(paths.DataDirectory))
        {
            throw new SetupException(
                SetupErrorCodes.RecoveryRequired,
                "완료된 이전 설치 기록과 실제 Agent 프로그램 또는 데이터 폴더가 일치하지 않습니다.");
        }

        foreach (var obsolete in new[]
                 {
                     pending.BackupDirectory,
                     pending.StagingDirectory,
                     pending.FailedDirectory
                 })
        {
            try
            {
                if (fileSystem.DirectoryExists(obsolete))
                {
                    fileSystem.DeleteDirectory(obsolete, recursive: true);
                }
            }
            catch
            {
                // A committed installation remains authoritative. Preserve leftovers.
            }
        }

        try
        {
            journalStore.Delete();
        }
        catch (Exception exception)
        {
            throw new SetupException(
                SetupErrorCodes.RecoveryRequired,
                "완료된 이전 설치 작업 기록을 정리할 수 없습니다. 다음 실행에서 다시 시도합니다.",
                exception);
        }

        steps.Add(new SetupStepResult(
            "COMMITTED_TRANSACTION_CLEANED",
            "이전 작업 정리",
            SetupStepState.Information,
            "완료된 이전 설치 작업 기록을 정리했습니다."));
    }

    private static void DeleteJournalAfterRollback(
        DeploymentJournalStore journalStore,
        DeploymentJournal? journal,
        string? rollbackCode)
    {
        if (journal is not null && rollbackCode is null)
        {
            try
            {
                journalStore.Delete();
            }
            catch
            {
                // A later run can safely repeat the completed rollback.
            }
        }
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
        DeploymentJournal pending)
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
            throw new SetupException(
                SetupErrorCodes.RecoveryRequired,
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
