using System.IO.Pipes;
using SamsungSwitchWatch.Viewer.Setup.Deployment;
using SamsungSwitchWatch.Viewer.Setup.Infrastructure;

namespace SamsungSwitchWatch.Viewer.Setup.Tests;

public sealed class ViewerShutdownCoordinatorTests
{
    [Fact]
    public void IsCurrentSession_RejectsDifferentWindowsSession()
    {
        Assert.True(ViewerShutdownCoordinator.IsCurrentSession(3, 3));
        Assert.False(ViewerShutdownCoordinator.IsCurrentSession(4, 3));
    }

    [Fact]
    public async Task EnsureStopped_WhenNoProcessOrMutex_ReturnsAlreadyStopped()
    {
        var coordinator = NewCoordinator(() => false);

        var result = await coordinator.EnsureStoppedAsync(TimeSpan.FromSeconds(1), default);

        Assert.Equal(ViewerShutdownStatus.AlreadyStopped, result.Status);
    }

    [Fact]
    public async Task EnsureStopped_AcceptedAck_WaitsForProcessExit()
    {
        var processRunning = true;
        var pipeName = "SSW-ViewerSetup-" + Guid.NewGuid().ToString("N");
        var mutexName = @"Local\SSW-ViewerSetup-" + Guid.NewGuid().ToString("N");
        var server = RunDuplexServerAsync(pipeName, request =>
        {
            Assert.Equal(ViewerShutdownCoordinator.ShutdownRequest, request);
            processRunning = false;
            return ViewerShutdownCoordinator.ShutdownAccepted;
        });
        var coordinator = new ViewerShutdownCoordinator(
            pipeName,
            mutexName,
            () => processRunning);

        var result = await coordinator.EnsureStoppedAsync(TimeSpan.FromSeconds(2), default);
        await server;

        Assert.Equal(ViewerShutdownStatus.Stopped, result.Status);
    }

    [Fact]
    public async Task EnsureStopped_AcceptedAckWithoutProcessOrMutexExit_TimesOut()
    {
        var pipeName = "SSW-ViewerSetup-" + Guid.NewGuid().ToString("N");
        var mutexName = @"Local\SSW-ViewerSetup-" + Guid.NewGuid().ToString("N");
        var server = RunDuplexServerAsync(
            pipeName,
            _ => ViewerShutdownCoordinator.ShutdownAccepted);
        var coordinator = new ViewerShutdownCoordinator(
            pipeName,
            mutexName,
            () => true);

        var result = await coordinator.EnsureStoppedAsync(
            TimeSpan.FromMilliseconds(250),
            default);
        await server;

        Assert.Equal(ViewerShutdownStatus.TimedOut, result.Status);
    }

    [Fact]
    public async Task EnsureStopped_LegacyInputOnlyPipe_IsProtocolUnsupported()
    {
        var pipeName = "SSW-ViewerSetup-" + Guid.NewGuid().ToString("N");
        var mutexName = @"Local\SSW-ViewerSetup-" + Guid.NewGuid().ToString("N");
        var observed = new TaskCompletionSource<byte>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var server = Task.Run(async () =>
        {
            using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync();
            var request = new byte[1];
            if (await pipe.ReadAsync(request) == 1)
            {
                observed.TrySetResult(request[0]);
            }
        });
        var coordinator = new ViewerShutdownCoordinator(
            pipeName,
            mutexName,
            () => true);

        var result = await coordinator.EnsureStoppedAsync(TimeSpan.FromSeconds(2), default);
        await server.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(ViewerShutdownStatus.ProtocolUnsupported, result.Status);
        Assert.Equal(
            ViewerShutdownCoordinator.ActivationRequest,
            await observed.Task.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    private static ViewerShutdownCoordinator NewCoordinator(Func<bool> processProbe) =>
        new(
            "SSW-ViewerSetup-" + Guid.NewGuid().ToString("N"),
            @"Local\SSW-ViewerSetup-" + Guid.NewGuid().ToString("N"),
            processProbe);

    private static Task RunDuplexServerAsync(
        string pipeName,
        Func<byte, byte> response) =>
        Task.Run(async () =>
        {
            using var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync();
            var request = new byte[1];
            Assert.Equal(1, await pipe.ReadAsync(request));
            await pipe.WriteAsync(new byte[] { response(request[0]) });
            await pipe.FlushAsync();
        });
}
