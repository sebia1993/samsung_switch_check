using System.Windows;
using SamsungSwitchWatch.Viewer.Setup.Deployment;
using SamsungSwitchWatch.Viewer.Setup.Infrastructure;

namespace SamsungSwitchWatch.Viewer.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (ViewerSetupPackageSmokeCheck.IsRequested(e.Args))
        {
            Shutdown(ViewerSetupPackageSmokeCheck.Run(
                AppContext.BaseDirectory,
                key => Resources.Contains(key) ? Resources[key] : null));
            return;
        }

        base.OnStartup(e);

        var fileSystem = new PhysicalViewerSetupFileSystem();
        var paths = ViewerSetupPaths.ForCurrentUser(AppContext.BaseDirectory);
        var packageValidator = new ViewerPackageValidator(fileSystem);
        var processManager = new WindowsViewerProcessManager();
        var shutdown = new ViewerShutdownCoordinator();
        var shortcuts = new WindowsViewerShortcutManager(fileSystem);
        var orchestrator = new ViewerDeploymentOrchestrator(
            packageValidator,
            fileSystem,
            processManager,
            shutdown,
            shortcuts,
            new WindowsPerUserDeploymentLock(),
            paths);

        var mainWindow = new MainWindow(orchestrator);
        MainWindow = mainWindow;
        mainWindow.Show();
    }
}
