using SamsungSwitchWatch.Viewer.Setup.Deployment;

namespace SamsungSwitchWatch.Viewer.Setup.Tests;

public sealed class ViewerSetupPackageSmokeCheckTests
{
    [Fact]
    public void IsRequested_RequiresExactSingleArgument()
    {
        Assert.True(ViewerSetupPackageSmokeCheck.IsRequested(
            [ViewerSetupPackageSmokeCheck.Argument]));
        Assert.False(ViewerSetupPackageSmokeCheck.IsRequested([]));
        Assert.False(ViewerSetupPackageSmokeCheck.IsRequested(
            [ViewerSetupPackageSmokeCheck.Argument, "extra"]));
    }

    [Fact]
    public void Run_WithResourcesScreensAndPackageFiles_Succeeds()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        var resources = RequiredResources.ToHashSet(StringComparer.Ordinal);

        var result = ViewerSetupPackageSmokeCheck.Run(
            workspace.PackageDirectory,
            key => resources.Contains(key) ? new object() : null,
            _ => new MemoryStream([1]),
            File.Exists);

        Assert.Equal(ViewerSetupPackageSmokeCheck.SuccessExitCode, result);
    }

    [Fact]
    public void Run_WhenRequiredPackageFileIsMissing_Fails()
    {
        using var workspace = new TestWorkspace();
        workspace.CreatePackage();
        File.Delete(Path.Combine(
            workspace.PackageDirectory,
            ViewerSetupConstants.ViewerExecutableName));
        var resources = RequiredResources.ToHashSet(StringComparer.Ordinal);

        var result = ViewerSetupPackageSmokeCheck.Run(
            workspace.PackageDirectory,
            key => resources.Contains(key) ? new object() : null,
            _ => new MemoryStream([1]),
            File.Exists);

        Assert.Equal(
            ViewerSetupPackageSmokeCheck.PackageFileFailureExitCode,
            result);
    }

    private static readonly string[] RequiredResources =
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
}
