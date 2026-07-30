namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class AgentSetupPackageSmokeCheckTests
{
    private static readonly string[] RequiredResourceKeys =
    [
        "CanvasBrush",
        "SurfaceBrush",
        "TextBrush",
        "MutedTextBrush",
        "BorderBrush",
        "PrimaryBrush",
        "PrimaryHoverBrush",
        "CardStyle",
        "PrimaryButton",
        "SecondaryButton"
    ];

    private static readonly string[] RequiredPackageFiles =
    [
        "SamsungSwitchWatch.Agent.exe",
        "D3DCompiler_47_cor3.dll",
        "PenImc_cor3.dll",
        "PresentationNative_cor3.dll",
        "vcruntime140_cor3.dll",
        "wpfgfx_cor3.dll",
        "BUILD-MANIFEST.json"
    ];

    [Fact]
    public void IsRequested_AcceptsOnlyExactSingleArgument()
    {
        Assert.True(AgentSetupPackageSmokeCheck.IsRequested(["--package-smoke-check"]));

        Assert.False(AgentSetupPackageSmokeCheck.IsRequested([]));
        Assert.False(AgentSetupPackageSmokeCheck.IsRequested(["--PACKAGE-SMOKE-CHECK"]));
        Assert.False(AgentSetupPackageSmokeCheck.IsRequested(
            ["--package-smoke-check", "--extra"]));
        Assert.False(AgentSetupPackageSmokeCheck.IsRequested(
            ["--package-smoke-check=true"]));
    }

    [Fact]
    public void Run_ValidResourcesAndPackageFiles_ReturnsSuccess()
    {
        var resources = RequiredResourceKeys.ToDictionary(
            key => (object)key,
            _ => (object)new object());
        var openedResources = new List<Uri>();
        var checkedFiles = new List<string>();

        var exitCode = AgentSetupPackageSmokeCheck.Run(
            @"C:\synthetic-package",
            key => resources.GetValueOrDefault(key),
            uri =>
            {
                openedResources.Add(uri);
                return new MemoryStream([1]);
            },
            path =>
            {
                checkedFiles.Add(Path.GetFileName(path));
                return true;
            });

        Assert.Equal(AgentSetupPackageSmokeCheck.SuccessExitCode, exitCode);
        Assert.Single(openedResources);
        Assert.Equal(
            "/SamsungSwitchWatch.Agent.Setup;component/MainWindow.xaml",
            openedResources[0].OriginalString);
        Assert.Equal(RequiredPackageFiles, checkedFiles);
    }

    [Fact]
    public void Run_MissingApplicationResource_ReturnsStableExitCode()
    {
        var resources = RequiredResourceKeys
            .Where(key => key != "PrimaryButton")
            .ToDictionary(key => (object)key, _ => (object)new object());

        var exitCode = AgentSetupPackageSmokeCheck.Run(
            @"C:\synthetic-package",
            key => resources.GetValueOrDefault(key),
            _ => new MemoryStream([1]),
            _ => true);

        Assert.Equal(
            AgentSetupPackageSmokeCheck.ApplicationResourceFailureExitCode,
            exitCode);
    }

    [Fact]
    public void Run_MissingScreenResource_ReturnsStableExitCode()
    {
        var exitCode = AgentSetupPackageSmokeCheck.Run(
            @"C:\synthetic-package",
            _ => new object(),
            _ => null,
            _ => true);

        Assert.Equal(
            AgentSetupPackageSmokeCheck.ScreenResourceFailureExitCode,
            exitCode);
    }

    [Fact]
    public void Run_MissingPackageFile_ReturnsStableExitCode()
    {
        var exitCode = AgentSetupPackageSmokeCheck.Run(
            @"C:\synthetic-package",
            _ => new object(),
            _ => new MemoryStream([1]),
            path => Path.GetFileName(path) != "SamsungSwitchWatch.Agent.exe");

        Assert.Equal(
            AgentSetupPackageSmokeCheck.PackageFileFailureExitCode,
            exitCode);
    }

    [Fact]
    public void Run_UnexpectedFailure_ReturnsStableExitCode()
    {
        var exitCode = AgentSetupPackageSmokeCheck.Run(
            @"C:\synthetic-package",
            _ => throw new InvalidOperationException("synthetic"),
            _ => new MemoryStream([1]),
            _ => true);

        Assert.Equal(
            AgentSetupPackageSmokeCheck.UnexpectedFailureExitCode,
            exitCode);
    }
}
