using SamsungSwitchWatch.Viewer.Setup.Deployment;
using SamsungSwitchWatch.Viewer.Setup.Infrastructure;

namespace SamsungSwitchWatch.Viewer.Setup.Tests;

public sealed class WindowsViewerShortcutManagerTests
{
    [Fact]
    public void Create_SameExecutableNameAtDifferentPath_PreservesExistingShortcut()
    {
        using var workspace = new TestWorkspace();
        var manager = new WindowsViewerShortcutManager(workspace.FileSystem);
        var shortcut = workspace.Paths.DesktopShortcutPath;
        var unrelatedTarget = Path.Combine(
            workspace.Root,
            "unrelated",
            ViewerSetupConstants.ViewerExecutableName);
        var canonicalTarget = Path.Combine(
            workspace.InstallDirectory,
            ViewerSetupConstants.ViewerExecutableName);
        manager.Create(shortcut, unrelatedTarget, Path.GetDirectoryName(unrelatedTarget)!);
        var original = File.ReadAllBytes(shortcut);

        var result = manager.Create(
            shortcut,
            canonicalTarget,
            workspace.InstallDirectory);

        Assert.Equal(
            ViewerShortcutMutationStatus.PreservedUnowned,
            result.Status);
        Assert.Equal(original, File.ReadAllBytes(shortcut));
    }

    [Fact]
    public void RemoveOwned_OnlyRemovesExactCanonicalTarget()
    {
        using var workspace = new TestWorkspace();
        var manager = new WindowsViewerShortcutManager(workspace.FileSystem);
        var shortcut = workspace.Paths.StartupShortcutPath;
        var canonicalTarget = Path.Combine(
            workspace.InstallDirectory,
            ViewerSetupConstants.ViewerExecutableName);
        manager.Create(shortcut, canonicalTarget, workspace.InstallDirectory);

        var result = manager.RemoveOwned(shortcut, canonicalTarget);

        Assert.Equal(ViewerShortcutMutationStatus.RemovedOwned, result.Status);
        Assert.False(File.Exists(shortcut));
    }

    [Fact]
    public void Restore_WhenShortcutWasCreatedExternallyAfterCapture_PreservesIt()
    {
        using var workspace = new TestWorkspace();
        var manager = new WindowsViewerShortcutManager(workspace.FileSystem);
        var shortcut = workspace.Paths.DesktopShortcutPath;
        var canonicalTarget = workspace.Paths.ViewerExecutablePath;
        var unrelatedTarget = Path.Combine(
            workspace.Root,
            "unrelated",
            ViewerSetupConstants.ViewerExecutableName);
        var snapshot = manager.Capture(
            shortcut,
            Path.Combine(workspace.Root, "evidence", "desktop.lnk"),
            canonicalTarget);
        manager.Create(
            shortcut,
            unrelatedTarget,
            Path.GetDirectoryName(unrelatedTarget)!);

        manager.Restore(snapshot);

        var ownership = manager.RemoveOwned(shortcut, unrelatedTarget);
        Assert.Equal(ViewerShortcutMutationStatus.RemovedOwned, ownership.Status);
    }

    [Fact]
    public void Restore_WhenOwnedShortcutWasReplacedExternally_PreservesReplacement()
    {
        using var workspace = new TestWorkspace();
        var manager = new WindowsViewerShortcutManager(workspace.FileSystem);
        var shortcut = workspace.Paths.StartMenuShortcutPath;
        var canonicalTarget = workspace.Paths.ViewerExecutablePath;
        var unrelatedTarget = Path.Combine(
            workspace.Root,
            "replacement",
            ViewerSetupConstants.ViewerExecutableName);
        manager.Create(shortcut, canonicalTarget, workspace.InstallDirectory);
        var snapshot = manager.Capture(
            shortcut,
            Path.Combine(workspace.Root, "evidence", "start-menu.lnk"),
            canonicalTarget);
        File.Delete(shortcut);
        manager.Create(
            shortcut,
            unrelatedTarget,
            Path.GetDirectoryName(unrelatedTarget)!);

        manager.Restore(snapshot);

        var ownership = manager.RemoveOwned(shortcut, unrelatedTarget);
        Assert.Equal(ViewerShortcutMutationStatus.RemovedOwned, ownership.Status);
    }
}
