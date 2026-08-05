using System.Diagnostics;
using SamsungSwitchWatch.Viewer.Setup.Deployment;
using SamsungSwitchWatch.Viewer.Setup.Infrastructure;

namespace SamsungSwitchWatch.Viewer.Setup.Tests;

public sealed class WindowsViewerProcessManagerTests
{
    private static readonly string SyntheticExecutablePath = Path.Combine(
        Path.GetTempPath(),
        "SamsungSwitchWatch.Viewer.synthetic.exe");

    [Theory]
    [InlineData(0, true, ViewerSetupErrorCodes.Ok)]
    [InlineData(7, false, ViewerSetupErrorCodes.SmokeFailed)]
    public async Task SmokeCheck_ExitedProcess_PreservesExitCodeContract(
        int exitCode,
        bool expectedSuccess,
        string expectedCode)
    {
        var manager = new WindowsViewerProcessManager(
            _ => StartCommand($"exit /b {exitCode}"));

        var result = await manager.RunSmokeCheckAsync(
            SyntheticExecutablePath,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(expectedSuccess, result.Succeeded);
        Assert.Equal(expectedCode, result.Code);
    }

    [Fact]
    public async Task SmokeCheck_Timeout_TerminatesAndConfirmsStartedProcessExit()
    {
        var started = NewStartedProcessSignal();
        var manager = new WindowsViewerProcessManager(
            _ => StartLongRunningCommand(started));
        var processId = 0;
        try
        {
            var operation = manager.RunSmokeCheckAsync(
                SyntheticExecutablePath,
                TimeSpan.FromMilliseconds(150),
                CancellationToken.None);
            processId = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var result = await operation;

            Assert.False(result.Succeeded);
            Assert.Equal(ViewerSetupErrorCodes.SmokeFailed, result.Code);
            AssertProcessExited(processId);
        }
        finally
        {
            KillProcessIfRunning(processId);
        }
    }

    [Fact]
    public async Task SmokeCheck_CallerCancellation_TerminatesBeforeCancellationReturns()
    {
        var started = NewStartedProcessSignal();
        var manager = new WindowsViewerProcessManager(
            _ => StartLongRunningCommand(started));
        using var cancellation = new CancellationTokenSource();
        var processId = 0;
        try
        {
            var operation = manager.RunSmokeCheckAsync(
                SyntheticExecutablePath,
                TimeSpan.FromSeconds(30),
                cancellation.Token);
            processId = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await operation);
            AssertProcessExited(processId);
        }
        finally
        {
            KillProcessIfRunning(processId);
        }
    }

    [Fact]
    public async Task Launch_CallerCancellation_TerminatesBeforeCancellationReturns()
    {
        var started = NewStartedProcessSignal();
        var manager = new WindowsViewerProcessManager(
            _ => StartLongRunningCommand(started));
        using var cancellation = new CancellationTokenSource();
        var processId = 0;
        try
        {
            var operation = manager.LaunchAndVerifyAsync(
                SyntheticExecutablePath,
                TimeSpan.FromSeconds(30),
                cancellation.Token);
            processId = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await operation);
            AssertProcessExited(processId);
        }
        finally
        {
            KillProcessIfRunning(processId);
        }
    }

    [Fact]
    public async Task Launch_LiveProcess_PreservesSuccessAndLeavesViewerRunning()
    {
        var started = NewStartedProcessSignal();
        var manager = new WindowsViewerProcessManager(
            _ => StartLongRunningCommand(started));
        var processId = 0;
        try
        {
            var operation = manager.LaunchAndVerifyAsync(
                SyntheticExecutablePath,
                TimeSpan.FromMilliseconds(150),
                CancellationToken.None);
            processId = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var result = await operation;

            Assert.True(result.Succeeded);
            Assert.Equal(ViewerSetupErrorCodes.Ok, result.Code);
            AssertProcessRunning(processId);
        }
        finally
        {
            KillProcessIfRunning(processId);
        }
    }

    [Fact]
    public async Task Launch_EarlyExit_ReturnsFailureWithNoRemainingProcess()
    {
        var started = NewStartedProcessSignal();
        var manager = new WindowsViewerProcessManager(_ =>
        {
            var process = StartCommand("exit /b 7");
            Assert.True(process.WaitForExit(5000));
            started.TrySetResult(process.Id);
            return process;
        });
        var processId = 0;
        try
        {
            var operation = manager.LaunchAndVerifyAsync(
                SyntheticExecutablePath,
                TimeSpan.FromMilliseconds(150),
                CancellationToken.None);
            processId = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var result = await operation;

            Assert.False(result.Succeeded);
            Assert.Equal(ViewerSetupErrorCodes.LaunchFailed, result.Code);
            AssertProcessExited(processId);
        }
        finally
        {
            KillProcessIfRunning(processId);
        }
    }

    private static TaskCompletionSource<int> NewStartedProcessSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Process StartLongRunningCommand(
        TaskCompletionSource<int> started)
    {
        var process = StartCommand("ping -n 60 127.0.0.1 > nul");
        started.TrySetResult(process.Id);
        return process;
    }

    private static Process StartCommand(string command)
    {
        var commandProcessor = Environment.GetEnvironmentVariable("ComSpec");
        if (string.IsNullOrWhiteSpace(commandProcessor))
        {
            commandProcessor = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "cmd.exe");
        }

        return Process.Start(new ProcessStartInfo
        {
            FileName = commandProcessor,
            Arguments = $"/d /c {command}",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("The synthetic child process did not start.");
    }

    private static void AssertProcessExited(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            Assert.True(process.HasExited, $"Process {processId} is still running.");
        }
        catch (ArgumentException)
        {
            // Windows removes a terminated process from the process table.
        }
    }

    private static void AssertProcessRunning(int processId)
    {
        using var process = Process.GetProcessById(processId);
        Assert.False(process.HasExited);
    }

    private static void KillProcessIfRunning(int processId)
    {
        if (processId <= 0)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
        }
    }
}
