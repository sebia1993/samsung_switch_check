using System.ComponentModel;
using System.Diagnostics;
using SamsungSwitchWatch.Viewer.Setup.Deployment;

namespace SamsungSwitchWatch.Viewer.Setup.Infrastructure;

public sealed class WindowsViewerProcessManager : IViewerProcessManager
{
    private static readonly TimeSpan ProcessCleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly Func<ProcessStartInfo, Process?> _startProcess;

    public WindowsViewerProcessManager()
        : this(StartProcess)
    {
    }

    internal WindowsViewerProcessManager(
        Func<ProcessStartInfo, Process?> startProcess)
    {
        _startProcess = startProcess ?? throw new ArgumentNullException(nameof(startProcess));
    }

    public async Task<ViewerProcessCheckResult> RunSmokeCheckAsync(
        string executablePath,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = _startProcess(new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = ViewerSetupConstants.InstallSmokeArgument,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
        if (process is null)
        {
            return new ViewerProcessCheckResult(
                false,
                ViewerSetupErrorCodes.SmokeFailed);
        }

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeoutSource.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new ViewerProcessCheckResult(
                    false,
                    ViewerSetupErrorCodes.SmokeFailed);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new ViewerProcessCheckResult(
                process.ExitCode == 0,
                process.ExitCode == 0
                    ? ViewerSetupErrorCodes.Ok
                    : ViewerSetupErrorCodes.SmokeFailed);
        }
        finally
        {
            await StopStartedProcessAsync(
                    process,
                    ViewerSetupErrorCodes.SmokeFailed)
                .ConfigureAwait(false);
        }
    }

    public async Task<ViewerProcessCheckResult> LaunchAndVerifyAsync(
        string executablePath,
        TimeSpan livenessWindow,
        CancellationToken cancellationToken)
    {
        using var process = _startProcess(new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = true
        });
        if (process is null)
        {
            return new ViewerProcessCheckResult(
                false,
                ViewerSetupErrorCodes.LaunchFailed);
        }

        var keepRunning = false;
        try
        {
            await Task.Delay(livenessWindow, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            keepRunning = !process.HasExited;
            return new ViewerProcessCheckResult(
                keepRunning,
                keepRunning ? ViewerSetupErrorCodes.Ok : ViewerSetupErrorCodes.LaunchFailed);
        }
        finally
        {
            if (!keepRunning)
            {
                await StopStartedProcessAsync(
                        process,
                        ViewerSetupErrorCodes.LaunchFailed)
                    .ConfigureAwait(false);
            }
        }
    }

    private static Process? StartProcess(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo);
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task StopStartedProcessAsync(
        Process process,
        string failureCode)
    {
        Exception? terminationFailure = null;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException)
        {
            terminationFailure = exception;
        }

        using var cleanupDeadline = new CancellationTokenSource(ProcessCleanupTimeout);
        try
        {
            await process.WaitForExitAsync(cleanupDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw CleanupFailure(failureCode, terminationFailure);
        }
        catch (Exception exception) when (
            exception is Win32Exception or InvalidOperationException)
        {
            throw CleanupFailure(failureCode, terminationFailure ?? exception);
        }

        try
        {
            if (!process.HasExited)
            {
                throw CleanupFailure(failureCode, terminationFailure);
            }
        }
        catch (InvalidOperationException exception)
        {
            throw CleanupFailure(failureCode, terminationFailure ?? exception);
        }
    }

    private static ViewerSetupException CleanupFailure(
        string failureCode,
        Exception? innerException) =>
        new(
            failureCode,
            "설치기가 시작한 Viewer 프로세스의 종료를 확인하지 못했습니다.",
            innerException);
}
