using SamsungSwitchWatch.Viewer.Setup.Deployment;
using SamsungSwitchWatch.Viewer.Setup.Infrastructure;

namespace SamsungSwitchWatch.Viewer.Setup.Tests;

public sealed class ViewerSetupPolicyAndLockTests
{
    [Fact]
    public void ForCurrentUser_UsesFixedPerUserInstallAndPreservedDataRoots()
    {
        var package = Path.Combine(Path.GetTempPath(), "viewer-package");
        var paths = ViewerSetupPaths.ForCurrentUser(package);
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        Assert.Equal(
            Path.Combine(
                localAppData,
                "Programs",
                "SamsungSwitchWatch",
                "Viewer"),
            paths.InstallDirectory);
        Assert.Equal(
            Path.Combine(localAppData, "SamsungSwitchWatch"),
            paths.DataDirectory);
    }

    [Fact]
    public void Buttons_WithPendingRecovery_DisablesInstallAndEnablesRecovery()
    {
        var recovery = new ViewerRecoveryInspection(
            true,
            true,
            ViewerSetupErrorCodes.RecoveryRequired,
            "pending");

        var state = ViewerSetupUiPolicy.Buttons(busy: false, recovery);

        Assert.False(state.InstallEnabled);
        Assert.True(state.RecoverEnabled);
        Assert.True(state.CloseEnabled);
    }

    [Fact]
    public void Result_UsesRecoverySpecificSuccessAndFailureText()
    {
        var success = ViewerSetupResult.Success("internal", []);
        var failure = ViewerSetupResult.Failure(
            ViewerSetupErrorCodes.RollbackFailed,
            "recover failed",
            []);

        var successPresentation = ViewerSetupUiPolicy.Result(
            ViewerSetupOperationKind.Recovery,
            success);
        var failurePresentation = ViewerSetupUiPolicy.Result(
            ViewerSetupOperationKind.Recovery,
            failure);

        Assert.Equal("복구 완료", successPresentation.Title);
        Assert.Contains("별도로 실행", successPresentation.Message);
        Assert.Equal("복구 실패", failurePresentation.Title);
        Assert.Equal("recover failed", failurePresentation.Message);
    }

    [Fact]
    public async Task PerUserDeploymentLock_CanBeReleasedAfterAsyncThreadSwitch()
    {
        var deploymentLock = new WindowsPerUserDeploymentLock();
        var lease = deploymentLock.Acquire();

        await Task.Run(lease.Dispose);

        using var reacquired = deploymentLock.Acquire();
    }

    [Fact]
    public void PerUserDeploymentLock_RejectsConcurrentAcquire()
    {
        var deploymentLock = new WindowsPerUserDeploymentLock();
        using var first = deploymentLock.Acquire();

        var exception = Assert.Throws<ViewerSetupException>(() =>
            deploymentLock.Acquire());

        Assert.Equal(ViewerSetupErrorCodes.AlreadyRunning, exception.Code);
    }

    [Theory]
    [InlineData(ViewerSetupErrorCodes.Cancelled)]
    [InlineData(ViewerSetupErrorCodes.AlreadyRunning)]
    [InlineData(ViewerSetupErrorCodes.LaunchFailed)]
    [InlineData(ViewerSetupErrorCodes.ShortcutFailed)]
    [InlineData(ViewerSetupErrorCodes.PathInvalid)]
    [InlineData(ViewerSetupErrorCodes.PathNotWritable)]
    public void PublicCode_PreservesActionableStableCodes(string code)
    {
        Assert.Equal(code, ViewerDeploymentOrchestrator.PublicCode(code));
    }
}
