using SamsungSwitchWatch.Agent.Setup.Deployment;
using SamsungSwitchWatch.Agent.Setup.Infrastructure;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class WindowsServiceManagerTests
{
    private const uint ServiceStopped = 0x00000001;
    private const uint ServiceStartPending = 0x00000002;
    private const uint ServiceStopPending = 0x00000003;
    private const uint ServiceRunning = 0x00000004;

    [Fact]
    public void CaptureAccess_UsesReadOnlyScmAndServiceRights()
    {
        Assert.Equal(0x00000001u, WindowsServiceManager.ScManagerConnectAccess);
        Assert.Equal(0x00020005u, WindowsServiceManager.ServiceCaptureAccess);
        const uint mutationRights =
            0x00000002 | // SERVICE_CHANGE_CONFIG
            0x00000010 | // SERVICE_START
            0x00000020 | // SERVICE_STOP
            0x00010000 | // DELETE
            0x00040000 | // WRITE_DAC
            0x00080000;  // WRITE_OWNER
        Assert.Equal(0u, WindowsServiceManager.ServiceCaptureAccess & mutationRights);
    }

    [Fact]
    public void CreateDisabledRecoveryPolicy_RemovesActionsAndNonCrashRestart()
    {
        var policy = WindowsServiceManager.CreateDisabledRecoveryPolicy();

        Assert.Equal(0u, policy.ResetPeriod);
        Assert.False(policy.ApplyOnNonCrashFailures);
        Assert.Empty(policy.Actions);
        Assert.Empty(policy.RebootMessage);
        Assert.Empty(policy.Command);
    }

    [Fact]
    public void EmptyRecoveryPolicy_AllocatesNonNullNativeActionBuffer()
    {
        var size = WindowsServiceManager.GetRecoveryActionsAllocationSize(0);

        Assert.True(size > 0);
    }

    [Fact]
    public void CreateAutomaticRecoveryPolicy_PreservesBoundedRestartSchedule()
    {
        var policy = WindowsServiceManager.CreateAutomaticRecoveryPolicy();

        Assert.Equal(86400u, policy.ResetPeriod);
        Assert.True(policy.ApplyOnNonCrashFailures);
        Assert.Equal(
            new[] { 1, 1, 1 },
            policy.Actions.Select(action => action.Type));
        Assert.Equal(
            new uint[] { 5000, 15000, 60000 },
            policy.Actions.Select(action => action.Delay));
    }

    [Theory]
    [InlineData(ServiceStopped, true, true)]
    [InlineData(ServiceStopped, false, false)]
    [InlineData(ServiceRunning, true, false)]
    [InlineData(ServiceStopPending, true, false)]
    public void IsStopComplete_RequiresStoppedScmStateAndEveryObservedProcessExit(
        uint currentState,
        bool processExited,
        bool expected)
    {
        var completed = WindowsServiceManager.IsStopComplete(
            currentState,
            [true, processExited]);

        Assert.Equal(expected, completed);
    }

    [Fact]
    public void ShouldRequestStop_SendsOncePerRunningProcessAndTracksRestartPid()
    {
        var requested = new HashSet<int>();

        Assert.True(WindowsServiceManager.ShouldRequestStop(
            ServiceRunning,
            100,
            requested));
        Assert.False(WindowsServiceManager.ShouldRequestStop(
            ServiceRunning,
            100,
            requested));
        Assert.True(WindowsServiceManager.ShouldRequestStop(
            ServiceRunning,
            101,
            requested));
        Assert.False(WindowsServiceManager.ShouldRequestStop(
            ServiceStopped,
            0,
            requested));
        Assert.False(WindowsServiceManager.ShouldRequestStop(
            ServiceStartPending,
            102,
            requested));
        Assert.False(WindowsServiceManager.ShouldRequestStop(
            ServiceStopPending,
            102,
            requested));
    }

    [Fact]
    public void FakeServiceManager_DisableAndConfigureRecoveryFollowContract()
    {
        var service = new FakeServiceManager(new ServiceSnapshot(
            true,
            false,
            "\"agent.exe\" --service",
            2,
            @"NT SERVICE\SamsungSwitchWatchAgent",
            SetupConstants.ServiceDisplayName,
            string.Empty,
            1,
            WindowsServiceManager.CreateAutomaticRecoveryPolicy(),
            [],
            0));

        service.DisableRecovery(SetupConstants.ServiceName);

        Assert.False(service.State.Recovery.ApplyOnNonCrashFailures);
        Assert.Empty(service.State.Recovery.Actions);
        Assert.Contains("recovery-disabled", service.Operations);

        service.ConfigureRecovery(SetupConstants.ServiceName);

        Assert.True(service.State.Recovery.ApplyOnNonCrashFailures);
        Assert.Equal(
            new uint[] { 5000, 15000, 60000 },
            service.State.Recovery.Actions.Select(action => action.Delay));
        Assert.Contains("recovery", service.Operations);
    }
}
