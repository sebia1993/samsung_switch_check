namespace SamsungSwitchWatch.Viewer.Setup.Deployment;

public sealed class ViewerDeploymentOrchestrator(
    IViewerPackageValidator packageValidator,
    IViewerSetupFileSystem fileSystem,
    IViewerProcessManager processManager,
    IViewerShutdownCoordinator shutdownCoordinator,
    IViewerShortcutManager shortcutManager,
    IViewerDeploymentLock deploymentLock,
    ViewerSetupPaths paths)
{
    private static readonly SemaphoreSlim ProcessGate = new(1, 1);
    private static readonly TimeSpan ViewerShutdownTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan SmokeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LaunchLivenessWindow = TimeSpan.FromSeconds(2);

    public async Task<ViewerSetupResult> DeployAsync(
        CancellationToken cancellationToken = default)
    {
        var steps = new ViewerSetupStepRecorder();
        var gateEntered = false;
        IDisposable? lease = null;
        try
        {
            await ProcessGate.WaitAsync(cancellationToken);
            gateEntered = true;
            lease = deploymentLock.Acquire();
            return await DeployCoreAsync(steps, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            steps.Failed(
                ViewerSetupErrorCodes.Cancelled,
                "Viewer 설치",
                "Viewer 설치가 취소되었습니다.");
            return Failure(
                ViewerSetupErrorCodes.Cancelled,
                "Viewer 설치가 취소되었습니다.",
                steps);
        }
        catch (ViewerSetupException exception)
        {
            steps.Failed(exception.Code, "Viewer 설치", exception.Message);
            return Failure(exception.Code, exception.Message, steps);
        }
        catch
        {
            steps.Failed(
                ViewerSetupErrorCodes.Unexpected,
                "Viewer 설치",
                "예상하지 못한 Windows 오류로 Viewer를 설치하지 못했습니다.");
            return Failure(
                ViewerSetupErrorCodes.Unexpected,
                "예상하지 못한 Windows 오류로 Viewer를 설치하지 못했습니다.",
                steps);
        }
        finally
        {
            lease?.Dispose();
            if (gateEntered)
            {
                ProcessGate.Release();
            }
        }
    }

    public ViewerRecoveryInspection InspectPendingRecovery()
    {
        var store = new ViewerDeploymentJournalStore(fileSystem, paths);
        if (!store.Exists)
        {
            return ViewerRecoveryInspection.None;
        }

        try
        {
            var journal = store.Read();
            return new ViewerRecoveryInspection(
                true,
                true,
                ViewerSetupErrorCodes.RecoveryRequired,
                journal.NormalLaunchObserved ||
                string.Equals(journal.Stage, "committed", StringComparison.Ordinal)
                    ? "이전 설치의 정리 작업이 남아 있습니다. 이전 상태 복구를 실행하세요."
                    : "완료되지 않은 Viewer 설치가 있습니다. 이전 상태 복구를 먼저 실행하세요.");
        }
        catch (ViewerSetupException exception)
        {
            return new ViewerRecoveryInspection(
                true,
                false,
                ViewerSetupErrorCodes.RecoveryRequired,
                exception.Message);
        }
        catch
        {
            return new ViewerRecoveryInspection(
                true,
                false,
                ViewerSetupErrorCodes.RecoveryRequired,
                "이전 Viewer 설치 작업을 안전하게 확인할 수 없습니다.");
        }
    }

    public async Task<ViewerSetupResult> RecoverAsync(
        CancellationToken cancellationToken = default)
    {
        var steps = new ViewerSetupStepRecorder();
        var gateEntered = false;
        IDisposable? lease = null;
        try
        {
            await ProcessGate.WaitAsync(cancellationToken);
            gateEntered = true;
            lease = deploymentLock.Acquire();

            var store = new ViewerDeploymentJournalStore(fileSystem, paths);
            if (!store.Exists)
            {
                steps.Succeeded(
                    "RECOVERY_NOT_REQUIRED",
                    "이전 상태 복구",
                    "복구가 필요한 이전 설치 작업이 없습니다.");
                return ViewerSetupResult.Success(
                    "복구가 필요한 이전 설치 작업이 없습니다.",
                    steps);
            }

            var journal = store.Read();
            await RecoverJournalAsync(store, journal, steps, cancellationToken);
            steps.Succeeded(
                "RECOVERY_COMPLETED",
                "이전 상태 복구",
                "이전 설치 상태 복구가 완료되었습니다.");
            return ViewerSetupResult.Success(
                "이전 설치 상태 복구가 완료되었습니다.",
                steps);
        }
        catch (OperationCanceledException)
        {
            steps.Failed(
                ViewerSetupErrorCodes.Cancelled,
                "이전 상태 복구",
                "Viewer 복구가 취소되었습니다.");
            return Failure(
                ViewerSetupErrorCodes.Cancelled,
                "Viewer 복구가 취소되었습니다.",
                steps);
        }
        catch (ViewerSetupException exception)
        {
            steps.Failed(exception.Code, "이전 상태 복구", exception.Message);
            return Failure(exception.Code, exception.Message, steps);
        }
        catch
        {
            steps.Failed(
                ViewerSetupErrorCodes.RollbackFailed,
                "이전 상태 복구",
                "이전 Viewer 설치 상태를 완전히 복구하지 못했습니다.");
            return Failure(
                ViewerSetupErrorCodes.RollbackFailed,
                "이전 Viewer 설치 상태를 완전히 복구하지 못했습니다.",
                steps);
        }
        finally
        {
            lease?.Dispose();
            if (gateEntered)
            {
                ProcessGate.Release();
            }
        }
    }

    private async Task<ViewerSetupResult> DeployCoreAsync(
        ViewerSetupStepRecorder steps,
        CancellationToken cancellationToken)
    {
        var store = new ViewerDeploymentJournalStore(fileSystem, paths);
        var ownsJournal = false;
        ViewerDeploymentJournal? journal = null;
        try
        {
            if (store.Exists)
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.RecoveryRequired,
                    "완료되지 않은 Viewer 설치가 있습니다. 이전 상태 복구를 먼저 실행하세요.");
            }

            ValidateBasePaths();
            var package = packageValidator.Validate(paths.PackageDirectory);
            steps.Succeeded(
                "PACKAGE_VALID",
                "패키지 확인",
                $"Viewer {package.Version} 파일 무결성이 정상입니다.");

            var existingPackage = ValidateExistingInstallation();

            var shutdown = await shutdownCoordinator.EnsureStoppedAsync(
                ViewerShutdownTimeout,
                cancellationToken);
            if (!shutdown.Succeeded)
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.ViewerRunning,
                    ShutdownMessage(shutdown.Status));
            }

            steps.Succeeded(
                "VIEWER_STOPPED",
                "실행 상태 확인",
                shutdown.Status == ViewerShutdownStatus.Stopped
                    ? "실행 중이던 Viewer가 안전하게 종료되었습니다."
                    : "실행 중인 Viewer가 없습니다.");

            var installParent = Path.GetDirectoryName(paths.InstallDirectory) ??
                                throw new ViewerSetupException(
                                    ViewerSetupErrorCodes.PathInvalid,
                                    "Viewer 설치 경로를 확인할 수 없습니다.");
            fileSystem.EnsureDirectoryWritable(installParent);
            fileSystem.EnsureDirectoryWritable(paths.OperationsDirectory);

            var transactionId = Guid.NewGuid().ToString("N");
            var transaction = paths.CreateTransactionPaths(transactionId);
            ViewerSetupPathGuard.ValidateTransactionPaths(
                paths,
                transactionId,
                transaction);
            EnsureTransactionTargetsAbsent(transaction);

            var desktopSnapshot = NewSnapshot(
                paths.DesktopShortcutPath,
                Path.Combine(transaction.EvidenceDirectory, "desktop.lnk"));
            var startMenuSnapshot = NewSnapshot(
                paths.StartMenuShortcutPath,
                Path.Combine(transaction.EvidenceDirectory, "start-menu.lnk"));
            var startupSnapshot = NewSnapshot(
                paths.StartupShortcutPath,
                Path.Combine(transaction.EvidenceDirectory, "startup.lnk"));

            journal = new ViewerDeploymentJournal(
                ViewerDeploymentJournalStore.CurrentFormatVersion,
                transactionId,
                "prepared",
                package.Version,
                package.ManifestSha256,
                existingPackage?.ManifestSha256,
                transaction.StagingDirectory,
                transaction.BackupDirectory,
                transaction.FailedDirectory,
                transaction.EvidenceDirectory,
                fileSystem.DirectoryExists(paths.InstallDirectory),
                false,
                false,
                desktopSnapshot,
                startMenuSnapshot,
                startupSnapshot,
                false,
                false,
                false,
                false);
            store.Write(journal);
            ownsJournal = true;

            fileSystem.CreateDirectory(transaction.EvidenceDirectory);
            desktopSnapshot = shortcutManager.Capture(
                desktopSnapshot.ShortcutPath,
                desktopSnapshot.BackupFilePath,
                desktopSnapshot.ExpectedTargetPath);
            startMenuSnapshot = shortcutManager.Capture(
                startMenuSnapshot.ShortcutPath,
                startMenuSnapshot.BackupFilePath,
                startMenuSnapshot.ExpectedTargetPath);
            startupSnapshot = shortcutManager.Capture(
                startupSnapshot.ShortcutPath,
                startupSnapshot.BackupFilePath,
                startupSnapshot.ExpectedTargetPath);
            journal = journal with
            {
                Stage = "shortcut-snapshots-captured",
                DesktopShortcut = desktopSnapshot,
                StartMenuShortcut = startMenuSnapshot,
                StartupShortcut = startupSnapshot
            };
            store.Write(journal);

            StagePackage(package, transaction.StagingDirectory);
            journal = journal with { Stage = "package-staged" };
            store.Write(journal);
            steps.Succeeded(
                "PACKAGE_STAGED",
                "파일 준비",
                "검증된 Viewer 파일을 별도 작업 폴더에 준비했습니다.");

            journal = journal with { Stage = "activation-started" };
            store.Write(journal);
            if (journal.PreviousInstallExisted)
            {
                journal = journal with
                {
                    Stage = "backup-move-intent",
                    InstallMovedToBackup = true
                };
                store.Write(journal);
                fileSystem.MoveDirectory(
                    paths.InstallDirectory,
                    transaction.BackupDirectory);
                ValidateExpectedInstallation(
                    transaction.BackupDirectory,
                    journal.PreviousManifestSha256!,
                    allowLegacy: true,
                    ViewerSetupErrorCodes.RollbackFailed);
            }

            journal = journal with
            {
                Stage = "activation-move-intent",
                StagingActivated = true
            };
            store.Write(journal);
            fileSystem.MoveDirectory(
                transaction.StagingDirectory,
                paths.InstallDirectory);
            ValidateExpectedInstallation(
                paths.InstallDirectory,
                journal.PackageManifestSha256,
                allowLegacy: false,
                ViewerSetupErrorCodes.InstallWriteFailed);
            journal = journal with
            {
                Stage = "files-activated"
            };
            store.Write(journal);

            var smoke = await processManager.RunSmokeCheckAsync(
                paths.ViewerExecutablePath,
                SmokeTimeout,
                cancellationToken);
            if (!smoke.Succeeded)
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.SmokeFailed,
                    "설치된 Viewer의 화면 리소스 사전 점검에 실패했습니다.");
            }

            journal = journal with { Stage = "smoke-passed" };
            store.Write(journal);
            steps.Succeeded(
                "SMOKE_PASSED",
                "Viewer 사전 점검",
                "설치된 Viewer의 화면 리소스 점검을 통과했습니다.");

            journal = ConfigureShortcuts(
                store,
                journal,
                steps,
                out var shortcutRecoveryFailed);
            if (shortcutRecoveryFailed)
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.ShortcutFailed,
                    "바로가기 복구를 완료하지 못해 Viewer 설치를 중단했습니다.");
            }

            journal = journal with { Stage = "normal-launch-started" };
            store.Write(journal);
            var launch = await processManager.LaunchAndVerifyAsync(
                paths.ViewerExecutablePath,
                LaunchLivenessWindow,
                cancellationToken);
            if (!launch.Succeeded)
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.LaunchFailed,
                    "설치된 Viewer가 정상 실행 상태를 유지하지 못했습니다.");
            }

            journal = journal with
            {
                Stage = "committed",
                NormalLaunchObserved = true
            };
            store.Write(journal);
            steps.Succeeded(
                "VIEWER_LAUNCHED",
                "Viewer 실행",
                "설치된 Viewer가 정상적으로 실행되었습니다.");

            try
            {
                CleanupCommittedTransaction(store, journal);
            }
            catch
            {
                steps.Warning(
                    "COMMIT_CLEANUP_PENDING",
                    "설치 정리",
                    "Viewer는 실행 중이지만 이전 설치의 정리 작업이 남았습니다. 다음 실행에서 이전 상태 복구를 선택하세요.");
            }

            return ViewerSetupResult.Success(
                "Viewer 설치 또는 업데이트가 완료되었습니다.",
                steps);
        }
        catch
        {
            if (ownsJournal && journal is not null && !journal.NormalLaunchObserved)
            {
                try
                {
                    await RecoverJournalAsync(
                        store,
                        store.Exists ? store.Read() : journal,
                        steps,
                        CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    throw new ViewerSetupException(
                        ViewerSetupErrorCodes.RollbackFailed,
                        "이전 Viewer 설치 상태를 완전히 복구하지 못했습니다.",
                        rollbackException);
                }
            }

            throw;
        }
    }

    private async Task RecoverJournalAsync(
        ViewerDeploymentJournalStore store,
        ViewerDeploymentJournal journal,
        ViewerSetupStepRecorder steps,
        CancellationToken cancellationToken)
    {
        ViewerSetupPathGuard.ValidateJournal(paths, journal);
        if (journal.NormalLaunchObserved ||
            string.Equals(journal.Stage, "committed", StringComparison.Ordinal))
        {
            CleanupCommittedTransaction(store, journal);
            return;
        }

        if (string.Equals(
                journal.Stage,
                "rollback-restored",
                StringComparison.Ordinal))
        {
            ValidateRestoredInstallation(journal);
            CleanupTransactionArtifacts(store, journal);
            return;
        }

        if (journal.StagingActivated || journal.InstallMovedToBackup)
        {
            var shutdown = await shutdownCoordinator.EnsureStoppedAsync(
                ViewerShutdownTimeout,
                cancellationToken);
            if (!shutdown.Succeeded)
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.ViewerRunning,
                    ShutdownMessage(shutdown.Status));
            }
        }

        var backupExists = fileSystem.DirectoryExists(journal.BackupDirectory);
        var installExists = fileSystem.DirectoryExists(paths.InstallDirectory);
        if (backupExists)
        {
            ValidateExpectedInstallation(
                journal.BackupDirectory,
                journal.PreviousManifestSha256!,
                allowLegacy: true,
                ViewerSetupErrorCodes.RollbackFailed);
            if (installExists)
            {
                if (fileSystem.DirectoryExists(journal.FailedDirectory))
                {
                    fileSystem.DeleteDirectory(journal.FailedDirectory, recursive: true);
                }

                fileSystem.MoveDirectory(
                    paths.InstallDirectory,
                    journal.FailedDirectory);
            }

            fileSystem.MoveDirectory(
                journal.BackupDirectory,
                paths.InstallDirectory);
            ValidateExpectedInstallation(
                paths.InstallDirectory,
                journal.PreviousManifestSha256!,
                allowLegacy: true,
                ViewerSetupErrorCodes.RollbackFailed);
        }
        else if (journal.PreviousInstallExisted)
        {
            var stagedFilesRemain =
                fileSystem.DirectoryExists(journal.StagingDirectory);
            var isolatedNewInstallRemains =
                fileSystem.DirectoryExists(journal.FailedDirectory);
            if (!installExists ||
                journal.StagingActivated &&
                !stagedFilesRemain &&
                !isolatedNewInstallRemains)
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.RollbackFailed,
                    "이전 Viewer 파일 백업 상태를 확인할 수 없어 복구를 중단했습니다.");
            }
            // The backup-move intent was persisted before the move. When the
            // backup is absent and activation never began, the original install
            // is still in place and must not be touched.
            ValidateExpectedInstallation(
                paths.InstallDirectory,
                journal.PreviousManifestSha256!,
                allowLegacy: true,
                ViewerSetupErrorCodes.RollbackFailed);
        }
        else if (journal.StagingActivated && installExists)
        {
            if (fileSystem.DirectoryExists(journal.FailedDirectory))
            {
                fileSystem.DeleteDirectory(journal.FailedDirectory, recursive: true);
            }

            fileSystem.MoveDirectory(
                paths.InstallDirectory,
                journal.FailedDirectory);
        }

        RestoreShortcutIfMutated(
            journal.DesktopShortcutMutated,
            journal.DesktopShortcut);
        RestoreShortcutIfMutated(
            journal.StartMenuShortcutMutated,
            journal.StartMenuShortcut);
        RestoreShortcutIfMutated(
            journal.StartupShortcutMutated,
            journal.StartupShortcut);

        journal = journal with
        {
            Stage = "rollback-restored",
            DesktopShortcutMutated = false,
            StartMenuShortcutMutated = false,
            StartupShortcutMutated = false
        };
        store.Write(journal);

        DeleteTransactionDirectory(journal.StagingDirectory);
        DeleteTransactionDirectory(journal.FailedDirectory);
        if (fileSystem.DirectoryExists(journal.BackupDirectory))
        {
            // A backup remains only when no move was necessary. It is still a
            // validated product transaction path, never the extraction folder.
            DeleteTransactionDirectory(journal.BackupDirectory);
        }

        DeleteTransactionDirectory(journal.EvidenceDirectory);
        store.Delete();
        steps.Succeeded(
            "ROLLBACK_COMPLETED",
            "자동 복구",
            "설치 전 Viewer 파일과 바로가기를 복구했습니다.");
    }

    private void StagePackage(ViewerPackage package, string stagingDirectory)
    {
        fileSystem.CreateDirectory(stagingDirectory);
        foreach (var packageFile in package.InstallFiles)
        {
            var destination = Path.Combine(stagingDirectory, packageFile.Name);
            fileSystem.CopyFile(packageFile.SourcePath, destination, overwrite: false);
            if (fileSystem.GetFileLength(destination) != packageFile.Size ||
                !string.Equals(
                    fileSystem.ComputeSha256(destination),
                    packageFile.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.InstallWriteFailed,
                    "Viewer 파일을 안전하게 준비하지 못했습니다.");
            }
        }

        fileSystem.CopyFile(
            package.ManifestPath,
            Path.Combine(stagingDirectory, ViewerSetupConstants.ManifestFileName),
            overwrite: false);
        var stagedManifest = Path.Combine(
            stagingDirectory,
            ViewerSetupConstants.ManifestFileName);
        if (!string.Equals(
                fileSystem.ComputeSha256(stagedManifest),
                package.ManifestSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.InstallWriteFailed,
                "Viewer 빌드 정보를 안전하게 준비하지 못했습니다.");
        }
    }

    private ViewerDeploymentJournal ConfigureShortcuts(
        ViewerDeploymentJournalStore store,
        ViewerDeploymentJournal journal,
        ViewerSetupStepRecorder steps,
        out bool recoveryFailed)
    {
        recoveryFailed = false;
        journal = ConfigureShortcut(
            store,
            journal,
            journal.DesktopShortcut,
            "desktop",
            current => current with { DesktopShortcutMutated = true },
            current => current with { DesktopShortcutMutated = false },
            () => shortcutManager.Create(
                paths.DesktopShortcutPath,
                paths.ViewerExecutablePath,
                paths.InstallDirectory),
            steps,
            ref recoveryFailed);
        journal = ConfigureShortcut(
            store,
            journal,
            journal.StartMenuShortcut,
            "start-menu",
            current => current with { StartMenuShortcutMutated = true },
            current => current with { StartMenuShortcutMutated = false },
            () => shortcutManager.Create(
                paths.StartMenuShortcutPath,
                paths.ViewerExecutablePath,
                paths.InstallDirectory),
            steps,
            ref recoveryFailed);
        journal = ConfigureShortcut(
            store,
            journal,
            journal.StartupShortcut,
            "startup",
            current => current with { StartupShortcutMutated = true },
            current => current with { StartupShortcutMutated = false },
            () => shortcutManager.RemoveOwned(
                paths.StartupShortcutPath,
                paths.ViewerExecutablePath),
            steps,
            ref recoveryFailed);

        journal = journal with { Stage = "shortcuts-configured" };
        store.Write(journal);
        return journal;
    }

    private ViewerDeploymentJournal ConfigureShortcut(
        ViewerDeploymentJournalStore store,
        ViewerDeploymentJournal journal,
        ShortcutJournalSnapshot snapshot,
        string diagnosticName,
        Func<ViewerDeploymentJournal, ViewerDeploymentJournal> markIntent,
        Func<ViewerDeploymentJournal, ViewerDeploymentJournal> clearIntent,
        Func<ViewerShortcutMutationResult> mutate,
        ViewerSetupStepRecorder steps,
        ref bool recoveryFailed)
    {
        journal = markIntent(journal);
        store.Write(journal);
        try
        {
            var result = mutate();
            if (!result.Mutated)
            {
                journal = clearIntent(journal);
                store.Write(journal);
            }

            if (result.Preserved)
            {
                steps.Warning(
                    "SHORTCUT_PRESERVED",
                    "바로가기",
                    "동일한 이름의 사용자 바로가기는 변경하지 않았습니다.");
            }
            else
            {
                steps.Succeeded(
                    $"SHORTCUT_{diagnosticName.ToUpperInvariant()}_OK",
                    "바로가기",
                    diagnosticName == "startup"
                        ? "제품 소유 자동 시작 바로가기를 정리했습니다."
                        : "제품 소유 바로가기를 준비했습니다.");
            }
        }
        catch
        {
            var restoreFailed = false;
            try
            {
                shortcutManager.Restore(snapshot);
                journal = clearIntent(journal);
                store.Write(journal);
            }
            catch
            {
                // Preserve the intent and evidence for a later explicit recovery.
                restoreFailed = true;
                recoveryFailed = true;
            }

            steps.Warning(
                ViewerSetupErrorCodes.ShortcutFailed,
                "바로가기",
                restoreFailed
                    ? "바로가기 복구를 완료하지 못해 Viewer 설치를 중단합니다."
                    : "바로가기를 변경하지 못했지만 Viewer 설치는 계속합니다.");
        }

        return journal;
    }

    private void CleanupCommittedTransaction(
        ViewerDeploymentJournalStore store,
        ViewerDeploymentJournal journal)
    {
        ValidateExpectedInstallation(
            paths.InstallDirectory,
            journal.PackageManifestSha256,
            allowLegacy: false,
            ViewerSetupErrorCodes.RollbackFailed);
        CleanupTransactionArtifacts(store, journal);
    }

    private void CleanupTransactionArtifacts(
        ViewerDeploymentJournalStore store,
        ViewerDeploymentJournal journal)
    {
        DeleteTransactionDirectory(journal.StagingDirectory);
        DeleteTransactionDirectory(journal.BackupDirectory);
        DeleteTransactionDirectory(journal.FailedDirectory);
        DeleteTransactionDirectory(journal.EvidenceDirectory);
        store.Delete();
    }

    private void DeleteTransactionDirectory(string directory)
    {
        if (fileSystem.DirectoryExists(directory))
        {
            fileSystem.DeleteDirectory(directory, recursive: true);
        }
    }

    private void RestoreShortcutIfMutated(
        bool mutated,
        ShortcutJournalSnapshot snapshot)
    {
        if (mutated)
        {
            shortcutManager.Restore(snapshot);
        }
    }

    private ShortcutJournalSnapshot NewSnapshot(
        string shortcutPath,
        string backupFilePath) =>
        new(
            shortcutPath,
            fileSystem.FileExists(shortcutPath),
            backupFilePath,
            paths.ViewerExecutablePath);

    private void EnsureTransactionTargetsAbsent(ViewerTransactionPaths transaction)
    {
        if (fileSystem.DirectoryExists(transaction.StagingDirectory) ||
            fileSystem.DirectoryExists(transaction.BackupDirectory) ||
            fileSystem.DirectoryExists(transaction.FailedDirectory) ||
            fileSystem.DirectoryExists(transaction.EvidenceDirectory))
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.InstallWriteFailed,
                "Viewer 설치 작업 폴더가 이미 사용 중입니다.");
        }
    }

    private void ValidateBasePaths()
    {
        var package = Normalize(paths.PackageDirectory);
        var install = Normalize(paths.InstallDirectory);
        var data = Normalize(paths.DataDirectory);
        var operations = Normalize(paths.OperationsDirectory);
        var installParent = Normalize(
            Path.GetDirectoryName(paths.InstallDirectory) ?? string.Empty);
        var packageManagedSibling = IsManagedTransactionSource(
            package,
            installParent,
            Path.GetFileName(install));
        if (string.Equals(package, install, StringComparison.OrdinalIgnoreCase) ||
            IsWithin(install, package) ||
            string.Equals(package, operations, StringComparison.OrdinalIgnoreCase) ||
            IsWithin(operations, package) ||
            packageManagedSibling ||
            string.Equals(data, install, StringComparison.OrdinalIgnoreCase) ||
            IsWithin(install, data))
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.PathInvalid,
                "Viewer 패키지, 설치 폴더 또는 데이터 폴더 경로가 안전하지 않습니다.");
        }
    }

    private ViewerPackage? ValidateExistingInstallation()
    {
        if (!fileSystem.DirectoryExists(paths.InstallDirectory) ||
            !fileSystem.DirectoryHasEntries(paths.InstallDirectory))
        {
            return null;
        }

        try
        {
            return packageValidator.ValidateExisting(paths.InstallDirectory);
        }
        catch (ViewerSetupException exception)
        {
            throw new ViewerSetupException(
                ViewerSetupErrorCodes.PathInvalid,
                "기존 Viewer 설치 폴더를 제품 소유의 완전한 설치로 확인할 수 없습니다.",
                exception);
        }
    }

    private void ValidateRestoredInstallation(ViewerDeploymentJournal journal)
    {
        if (!journal.PreviousInstallExisted)
        {
            if (fileSystem.DirectoryExists(paths.InstallDirectory))
            {
                throw new ViewerSetupException(
                    ViewerSetupErrorCodes.RollbackFailed,
                    "첫 설치 이전 상태가 완전히 복구되지 않았습니다.");
            }

            return;
        }

        ValidateExpectedInstallation(
            paths.InstallDirectory,
            journal.PreviousManifestSha256!,
            allowLegacy: true,
            ViewerSetupErrorCodes.RollbackFailed);
    }

    private ViewerPackage ValidateExpectedInstallation(
        string directory,
        string expectedManifestSha256,
        bool allowLegacy,
        string failureCode)
    {
        try
        {
            var package = allowLegacy
                ? packageValidator.ValidateExisting(directory)
                : packageValidator.Validate(directory);
            if (!string.Equals(
                    package.ManifestSha256,
                    expectedManifestSha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "The Viewer manifest changed during deployment.");
            }

            return package;
        }
        catch (Exception exception) when (
            exception is ViewerSetupException or InvalidDataException)
        {
            throw new ViewerSetupException(
                failureCode,
                "Viewer 설치 파일 무결성을 다시 확인하지 못해 작업을 중단했습니다.",
                exception);
        }
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsWithin(string parent, string candidate) =>
        candidate.StartsWith(
            parent + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsManagedTransactionSource(
        string package,
        string installParent,
        string installName)
    {
        var current = package;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                return false;
            }

            if (string.Equals(
                    Normalize(parent),
                    installParent,
                    StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(current);
                return name.StartsWith(
                        installName + ".__staging_",
                        StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(
                        installName + ".__backup_",
                        StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith(
                        installName + ".__failed_",
                        StringComparison.OrdinalIgnoreCase);
            }

            current = parent;
        }

        return false;
    }

    private static string ShutdownMessage(ViewerShutdownStatus status) =>
        status switch
        {
            ViewerShutdownStatus.Rejected =>
                "실행 중인 Viewer가 종료 요청을 거부했습니다. Viewer를 직접 닫고 다시 시도하세요.",
            ViewerShutdownStatus.ProtocolUnsupported =>
                "실행 중인 이전 Viewer는 자동 종료를 지원하지 않습니다. Viewer를 직접 닫고 다시 시도하세요.",
            ViewerShutdownStatus.TimedOut =>
                "Viewer 종료를 확인하지 못했습니다. Viewer를 직접 닫고 다시 시도하세요.",
            _ =>
                "실행 중인 Viewer와 안전하게 통신할 수 없습니다. Viewer를 직접 닫고 다시 시도하세요."
        };

    private static ViewerSetupResult Failure(
        string internalCode,
        string message,
        IReadOnlyList<ViewerSetupStep> steps) =>
        ViewerSetupResult.Failure(PublicCode(internalCode), message, steps);

    internal static string PublicCode(string internalCode) =>
        internalCode switch
        {
            ViewerSetupErrorCodes.PackageNotFound or
            ViewerSetupErrorCodes.ManifestInvalid or
            ViewerSetupErrorCodes.PackageHashMismatch or
            ViewerSetupErrorCodes.PackageInvalid =>
                ViewerSetupErrorCodes.PackageInvalid,
            ViewerSetupErrorCodes.ViewerRunning =>
                ViewerSetupErrorCodes.ViewerRunning,
            ViewerSetupErrorCodes.SmokeFailed =>
                ViewerSetupErrorCodes.SmokeFailed,
            ViewerSetupErrorCodes.RollbackFailed =>
                ViewerSetupErrorCodes.RollbackFailed,
            ViewerSetupErrorCodes.RecoveryRequired =>
                ViewerSetupErrorCodes.RecoveryRequired,
            ViewerSetupErrorCodes.Cancelled =>
                ViewerSetupErrorCodes.Cancelled,
            ViewerSetupErrorCodes.AlreadyRunning =>
                ViewerSetupErrorCodes.AlreadyRunning,
            ViewerSetupErrorCodes.LaunchFailed =>
                ViewerSetupErrorCodes.LaunchFailed,
            ViewerSetupErrorCodes.ShortcutFailed =>
                ViewerSetupErrorCodes.ShortcutFailed,
            ViewerSetupErrorCodes.PathInvalid =>
                ViewerSetupErrorCodes.PathInvalid,
            ViewerSetupErrorCodes.PathNotWritable =>
                ViewerSetupErrorCodes.PathNotWritable,
            _ => ViewerSetupErrorCodes.InstallWriteFailed
        };
}
