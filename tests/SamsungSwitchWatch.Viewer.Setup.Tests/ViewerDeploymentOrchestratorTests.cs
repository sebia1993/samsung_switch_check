using SamsungSwitchWatch.Viewer.Setup.Deployment;

namespace SamsungSwitchWatch.Viewer.Setup.Tests;

public sealed class ViewerDeploymentOrchestratorTests
{
    [Fact]
    public async Task Deploy_InstallsValidatedFiles_PreservesData_AndLeavesPackage()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        TestWorkspace.Write(
            Path.Combine(workspace.DataDirectory, "viewer-settings.json"),
            "preserve-me");
        TestWorkspace.Write(workspace.Paths.StartupShortcutPath, "owned:legacy-viewer");
        var packageViewer = Path.Combine(
            workspace.PackageDirectory,
            ViewerSetupConstants.ViewerExecutableName);
        var packageHash = TestWorkspace.Hash(packageViewer);

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
        Assert.Equal(ViewerSetupErrorCodes.Ok, result.Code);
        Assert.Equal(
            "preserve-me",
            File.ReadAllText(Path.Combine(
                workspace.DataDirectory,
                "viewer-settings.json")));
        Assert.True(File.Exists(Path.Combine(
            workspace.InstallDirectory,
            ViewerSetupConstants.ManifestFileName)));
        Assert.True(File.Exists(Path.Combine(
            workspace.InstallDirectory,
            ViewerSetupConstants.SetupExecutableName)));
        Assert.True(File.Exists(workspace.Paths.DesktopShortcutPath));
        Assert.True(File.Exists(workspace.Paths.StartMenuShortcutPath));
        Assert.False(File.Exists(workspace.Paths.StartupShortcutPath));
        Assert.True(File.Exists(packageViewer));
        Assert.Equal(packageHash, TestWorkspace.Hash(packageViewer));
        Assert.Equal(1, workspace.Process.SmokeCalls);
        Assert.Equal(1, workspace.Process.LaunchCalls);
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Theory]
    [InlineData(ViewerShutdownStatus.Rejected)]
    [InlineData(ViewerShutdownStatus.ProtocolUnsupported)]
    [InlineData(ViewerShutdownStatus.Unavailable)]
    [InlineData(ViewerShutdownStatus.TimedOut)]
    public async Task Deploy_WhenViewerCannotStop_FailsBeforeMutation(
        ViewerShutdownStatus status)
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        workspace.Shutdown.Status = status;

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.ViewerRunning, result.Code);
        Assert.False(Directory.Exists(workspace.InstallDirectory));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
        Assert.Equal(0, workspace.Process.SmokeCalls);
    }

    [Fact]
    public async Task Deploy_WhenSmokeFails_RestoresPreviousInstall()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        workspace.CreateInstalledProduct(viewerContents: "viewer-old");
        workspace.Process.SmokeSucceeds = false;

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.SmokeFailed, result.Code);
        Assert.Equal(
            "viewer-old",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Fact]
    public async Task Deploy_UpgradesExactLegacyViewerInstall()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        workspace.CreateLegacyInstalledProduct(viewerContents: "viewer-legacy");

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
        Assert.Equal(
            "viewer-new",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
        Assert.True(File.Exists(Path.Combine(
            workspace.InstallDirectory,
            ViewerSetupConstants.SetupExecutableName)));
    }

    [Fact]
    public async Task Deploy_WhenNormalLaunchFails_RestoresFilesAndShortcuts()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        workspace.CreateInstalledProduct(viewerContents: "viewer-old");
        TestWorkspace.Write(workspace.Paths.DesktopShortcutPath, "owned:old-desktop");
        TestWorkspace.Write(workspace.Paths.StartMenuShortcutPath, "owned:old-start");
        TestWorkspace.Write(workspace.Paths.StartupShortcutPath, "owned:old-startup");
        workspace.Process.LaunchSucceeds = false;

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.LaunchFailed, result.Code);
        Assert.Equal("owned:old-desktop", File.ReadAllText(workspace.Paths.DesktopShortcutPath));
        Assert.Equal("owned:old-start", File.ReadAllText(workspace.Paths.StartMenuShortcutPath));
        Assert.Equal("owned:old-startup", File.ReadAllText(workspace.Paths.StartupShortcutPath));
        Assert.Equal(
            "viewer-old",
            File.ReadAllText(Path.Combine(
                workspace.InstallDirectory,
                ViewerSetupConstants.ViewerExecutableName)));
    }

    [Fact]
    public async Task Deploy_ShortcutFailureIsWarning_NotCoreRollback()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        workspace.Shortcuts.FailDesktopCreate = true;

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.True(result.Succeeded, $"{result.Code}: {result.Message}");
        Assert.Contains(result.Steps, step =>
            step.Code == ViewerSetupErrorCodes.ShortcutFailed &&
            step.State == ViewerSetupStepState.Warning);
        Assert.True(File.Exists(Path.Combine(
            workspace.InstallDirectory,
            ViewerSetupConstants.ViewerExecutableName)));
    }

    [Fact]
    public async Task Deploy_ShortcutMutationAndRestoreFailure_PreservesRecoveryEvidence()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        workspace.Shortcuts.FailDesktopCreate = true;
        workspace.Shortcuts.FailRestore = true;

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.RollbackFailed, result.Code);
        Assert.True(File.Exists(workspace.Paths.JournalPath));
        var journal = new ViewerDeploymentJournalStore(
            workspace.FileSystem,
            workspace.Paths).Read();
        Assert.True(journal.DesktopShortcutMutated);
        Assert.True(Directory.Exists(journal.EvidenceDirectory));
    }

    [Fact]
    public async Task Deploy_PreservesUnownedShortcuts()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        TestWorkspace.Write(workspace.Paths.DesktopShortcutPath, "unowned:other.exe");
        TestWorkspace.Write(workspace.Paths.StartMenuShortcutPath, "unowned:other.exe");
        TestWorkspace.Write(workspace.Paths.StartupShortcutPath, "unowned:other.exe");

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("unowned:other.exe", File.ReadAllText(workspace.Paths.DesktopShortcutPath));
        Assert.Equal("unowned:other.exe", File.ReadAllText(workspace.Paths.StartMenuShortcutPath));
        Assert.Equal("unowned:other.exe", File.ReadAllText(workspace.Paths.StartupShortcutPath));
        Assert.Contains(result.Steps, step => step.Code == "SHORTCUT_PRESERVED");
    }

    [Fact]
    public async Task Deploy_RejectsUnknownNonEmptyCanonicalInstall()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        var foreign = Path.Combine(workspace.InstallDirectory, "foreign.txt");
        TestWorkspace.Write(foreign, "do-not-touch");

        var result = await workspace.CreateOrchestrator().DeployAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.PathInvalid, result.Code);
        Assert.Equal("do-not-touch", File.ReadAllText(foreign));
        Assert.False(File.Exists(workspace.Paths.JournalPath));
    }

    [Theory]
    [InlineData("install")]
    [InlineData("staging")]
    [InlineData("backup")]
    [InlineData("operations")]
    public async Task Deploy_RejectsManagedPackageSource(string sourceKind)
    {
        using var workspace = new TestWorkspace();
        var transactionId = new string('a', 32);
        var transaction = workspace.Paths.CreateTransactionPaths(transactionId);
        var packageDirectory = sourceKind switch
        {
            "install" => workspace.InstallDirectory,
            "staging" => Path.Combine(transaction.StagingDirectory, "extracted"),
            "backup" => transaction.BackupDirectory,
            "operations" => Path.Combine(workspace.OperationsDirectory, "download"),
            _ => throw new ArgumentOutOfRangeException(nameof(sourceKind))
        };
        workspace.CreatePackage(packageDirectory);
        var paths = workspace.CreatePaths(packageDirectory);

        var result = await workspace.CreateOrchestrator(paths).DeployAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ViewerSetupErrorCodes.PathInvalid, result.Code);
        Assert.True(File.Exists(Path.Combine(
            packageDirectory,
            ViewerSetupConstants.ViewerExecutableName)));
    }
}
