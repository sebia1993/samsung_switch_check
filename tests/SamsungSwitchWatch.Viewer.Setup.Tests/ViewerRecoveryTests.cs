using SamsungSwitchWatch.Viewer.Setup.Deployment;

namespace SamsungSwitchWatch.Viewer.Setup.Tests;

public sealed class ViewerRecoveryTests
{
    [Fact]
    public async Task Recover_BeforeBackupMove_KeepsExistingInstallAndCleansStaging()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateInstalledProduct(viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: false,
            stagingActivated: false);
        Directory.CreateDirectory(journal.StagingDirectory);
        TestWorkspace.Write(
            Path.Combine(journal.StagingDirectory, "partial.tmp"),
            "partial");
        WriteJournal(workspace, journal);

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(
            "viewer-old",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
        Assert.False(Directory.Exists(journal.StagingDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Recover_BackupMoveIntentWithoutMove_KeepsOriginalInstall()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateInstalledProduct(viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: false) with
        {
            Stage = "backup-move-intent"
        };
        WriteJournal(workspace, journal);

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(
            "viewer-old",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
    }

    [Fact]
    public async Task Recover_AfterActivation_IsolatesNewInstallAndRestoresBackup()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage(
            workspace.InstallDirectory,
            version: "0.11.4-poc",
            viewerContents: "viewer-new");
        var transaction = workspace.Paths.CreateTransactionPaths(new string('b', 32));
        workspace.CreatePackage(
            transaction.BackupDirectory,
            version: "0.11.3-poc",
            viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: true) with
        {
            Stage = "files-activated"
        };
        WriteJournal(workspace, journal);

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(
            "viewer-old",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
        Assert.False(Directory.Exists(journal.FailedDirectory));
        Assert.False(Directory.Exists(journal.BackupDirectory));
    }

    [Fact]
    public async Task Recover_FirstInstallActivationIntentBeforeMove_CleansStaging()
    {
        using var workspace = new TestWorkspace();
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: false,
            installMovedToBackup: false,
            stagingActivated: true) with
        {
            Stage = "activation-move-intent"
        };
        Directory.CreateDirectory(journal.StagingDirectory);
        TestWorkspace.Write(
            Path.Combine(journal.StagingDirectory, "new-viewer.tmp"),
            "staged");
        WriteJournal(workspace, journal);

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(result.Succeeded);
        Assert.False(Directory.Exists(journal.StagingDirectory));
        Assert.False(Directory.Exists(workspace.InstallDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Recover_CommittedCleanupPending_KeepsNewInstallAndOnlyCleansEvidence()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage(
            workspace.InstallDirectory,
            viewerContents: "viewer-new");
        var transaction = workspace.Paths.CreateTransactionPaths(new string('b', 32));
        workspace.CreatePackage(
            transaction.BackupDirectory,
            version: "0.11.3-poc",
            viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: true) with
        {
            Stage = "committed",
            NormalLaunchObserved = true
        };
        WriteJournal(workspace, journal);

        var inspection = workspace.CreateOrchestrator().InspectPendingRecovery();
        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(inspection.Exists);
        Assert.True(inspection.CanRecover);
        Assert.True(result.Succeeded);
        Assert.Equal(
            "viewer-new",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
        Assert.False(Directory.Exists(journal.BackupDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Recover_RollbackRestoredJournal_IsIdempotentCleanupOnly()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateInstalledProduct(viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: true) with
        {
            Stage = "rollback-restored"
        };
        Directory.CreateDirectory(journal.FailedDirectory);
        TestWorkspace.Write(
            Path.Combine(journal.FailedDirectory, "viewer-new.tmp"),
            "new");
        WriteJournal(workspace, journal);

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(result.Succeeded);
        Assert.Equal(
            "viewer-old",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
        Assert.False(Directory.Exists(journal.FailedDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Recover_CorruptedBackup_PreservesRecoveryEvidence()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage(
            workspace.InstallDirectory,
            viewerContents: "viewer-new");
        var transaction = workspace.Paths.CreateTransactionPaths(new string('b', 32));
        workspace.CreatePackage(
            transaction.BackupDirectory,
            version: "0.11.3-poc",
            viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: true) with
        {
            Stage = "files-activated"
        };
        WriteJournal(workspace, journal);
        TestWorkspace.Write(
            Path.Combine(
                journal.BackupDirectory,
                ViewerSetupConstants.ViewerExecutableName),
            "corrupted-backup");

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.RollbackFailed, result.Code);
        Assert.True(File.Exists(workspace.Paths.JournalPath));
        Assert.True(Directory.Exists(journal.BackupDirectory));
        Assert.Equal(
            "viewer-new",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
    }

    [Fact]
    public async Task Recover_CommittedCorruptedInstall_DoesNotDeleteBackup()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage(
            workspace.InstallDirectory,
            viewerContents: "viewer-new");
        var transaction = workspace.Paths.CreateTransactionPaths(new string('b', 32));
        workspace.CreatePackage(
            transaction.BackupDirectory,
            version: "0.11.3-poc",
            viewerContents: "viewer-old");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: true,
            installMovedToBackup: true,
            stagingActivated: true) with
        {
            Stage = "committed",
            NormalLaunchObserved = true
        };
        WriteJournal(workspace, journal);
        TestWorkspace.Write(
            Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName),
            "corrupted-current");

        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.RollbackFailed, result.Code);
        Assert.True(File.Exists(workspace.Paths.JournalPath));
        Assert.True(Directory.Exists(journal.BackupDirectory));
    }

    [Fact]
    public async Task Recover_MaliciousJournalPath_DoesNotDeleteArbitraryDirectory()
    {
        using var workspace = new TestWorkspace();
        var arbitrary = Path.Combine(workspace.Root, "do-not-delete");
        Directory.CreateDirectory(arbitrary);
        TestWorkspace.Write(Path.Combine(arbitrary, "keep.txt"), "keep");
        var journal = CreateJournal(
            workspace,
            previousInstallExisted: false,
            installMovedToBackup: false,
            stagingActivated: false) with
        {
            FailedDirectory = arbitrary
        };
        Directory.CreateDirectory(workspace.OperationsDirectory);
        File.WriteAllText(
            workspace.Paths.JournalPath,
            System.Text.Json.JsonSerializer.Serialize(journal));

        var inspection = workspace.CreateOrchestrator().InspectPendingRecovery();
        var result = await workspace.CreateOrchestrator().RecoverAsync();

        Assert.True(inspection.Exists);
        Assert.False(inspection.CanRecover);
        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.RecoveryRequired, result.Code);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(arbitrary, "keep.txt")));
    }

    private static ViewerDeploymentJournal CreateJournal(
        TestWorkspace workspace,
        bool previousInstallExisted,
        bool installMovedToBackup,
        bool stagingActivated)
    {
        var transactionId = new string('b', 32);
        var transaction = workspace.Paths.CreateTransactionPaths(transactionId);
        var desktop = new ShortcutJournalSnapshot(
            workspace.Paths.DesktopShortcutPath,
            false,
            Path.Combine(transaction.EvidenceDirectory, "desktop.lnk"),
            workspace.Paths.ViewerExecutablePath);
        var start = new ShortcutJournalSnapshot(
            workspace.Paths.StartMenuShortcutPath,
            false,
            Path.Combine(transaction.EvidenceDirectory, "start-menu.lnk"),
            workspace.Paths.ViewerExecutablePath);
        var startup = new ShortcutJournalSnapshot(
            workspace.Paths.StartupShortcutPath,
            false,
            Path.Combine(transaction.EvidenceDirectory, "startup.lnk"),
            workspace.Paths.ViewerExecutablePath);
        var packageManifestSha256 = fileSystemManifestHash(
            workspace.InstallDirectory,
            fallback: new string('c', 64));
        var previousManifestSha256 = previousInstallExisted
            ? fileSystemManifestHash(
                transaction.BackupDirectory,
                fileSystemManifestHash(
                    workspace.InstallDirectory,
                    new string('d', 64)))
            : null;
        return new ViewerDeploymentJournal(
            ViewerDeploymentJournalStore.CurrentFormatVersion,
            transactionId,
            "prepared",
            "0.11.4-poc",
            packageManifestSha256,
            previousManifestSha256,
            transaction.StagingDirectory,
            transaction.BackupDirectory,
            transaction.FailedDirectory,
            transaction.EvidenceDirectory,
            previousInstallExisted,
            installMovedToBackup,
            stagingActivated,
            desktop,
            start,
            startup,
            false,
            false,
            false,
            false);

        static string fileSystemManifestHash(string directory, string fallback)
        {
            var manifest = Path.Combine(directory, ViewerSetupConstants.ManifestFileName);
            return File.Exists(manifest)
                ? TestWorkspace.Hash(manifest)
                : fallback;
        }
    }

    private static void WriteJournal(
        TestWorkspace workspace,
        ViewerDeploymentJournal journal)
    {
        Directory.CreateDirectory(journal.EvidenceDirectory);
        new ViewerDeploymentJournalStore(workspace.FileSystem, workspace.Paths)
            .Write(journal);
    }
}
