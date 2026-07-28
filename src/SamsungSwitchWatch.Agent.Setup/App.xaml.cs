using System.Windows;
using SamsungSwitchWatch.Agent.Setup.Deployment;
using SamsungSwitchWatch.Agent.Setup.Infrastructure;

namespace SamsungSwitchWatch.Agent.Setup;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var fileSystem = new PhysicalSetupFileSystem();
        var paths = DeploymentPaths.ForCurrentMachine(AppContext.BaseDirectory);
        var services = new WindowsServiceManager();
        var firewall = new WindowsFirewallManager();
        var health = new HttpsAgentHealthProbe();
        var administrator = new WindowsAdministratorChecker();
        var package = new AgentPackageValidator(fileSystem);
        var networks = new WindowsNetworkDiscovery();
        var diagnostics = new SetupDiagnosticsService(
            package,
            fileSystem,
            services,
            firewall,
            health,
            administrator,
            paths);
        var deployment = new AgentDeploymentOrchestrator(
            package,
            fileSystem,
            services,
            firewall,
            health,
            administrator,
            new WindowsMachineDeploymentLock(),
            paths);

        var diagnosticsOnly = e.Args.Any(argument =>
            string.Equals(argument, "--diagnostics", StringComparison.OrdinalIgnoreCase));
        MainWindow = new MainWindow(networks, diagnostics, deployment, diagnosticsOnly);
        MainWindow.Show();
    }
}
