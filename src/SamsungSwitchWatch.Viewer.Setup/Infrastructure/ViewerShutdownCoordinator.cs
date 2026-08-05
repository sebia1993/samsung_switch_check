using System.Diagnostics;
using System.IO.Pipes;
using SamsungSwitchWatch.Viewer.Setup.Deployment;

namespace SamsungSwitchWatch.Viewer.Setup.Infrastructure;

public sealed class ViewerShutdownCoordinator : IViewerShutdownCoordinator
{
    internal const string DefaultPipeName = "SamsungSwitchWatch.Viewer.Activation";
    internal const string DefaultMutexName = @"Local\SamsungSwitchWatch.Viewer.Singleton";
    internal const byte ActivationRequest = 0x01;
    internal const byte ShutdownRequest = 0x02;
    internal const byte ShutdownAccepted = 0x81;
    internal const byte ShutdownRejected = 0x82;
    private readonly string _pipeName;
    private readonly string _mutexName;
    private readonly Func<bool> _processProbe;

    public ViewerShutdownCoordinator()
        : this(DefaultPipeName, DefaultMutexName, IsViewerProcessRunning)
    {
    }

    internal ViewerShutdownCoordinator(
        string pipeName,
        string mutexName,
        Func<bool> processProbe)
    {
        _pipeName = string.IsNullOrWhiteSpace(pipeName)
            ? throw new ArgumentException("Pipe name is required.", nameof(pipeName))
            : pipeName;
        _mutexName = string.IsNullOrWhiteSpace(mutexName)
            ? throw new ArgumentException("Mutex name is required.", nameof(mutexName))
            : mutexName;
        _processProbe = processProbe ?? throw new ArgumentNullException(nameof(processProbe));
    }

    public async Task<ViewerShutdownResult> EnsureStoppedAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        if (!_processProbe() && !IsSingleInstanceMutexHeld(_mutexName))
        {
            return new ViewerShutdownResult(ViewerShutdownStatus.AlreadyStopped);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        deadline.CancelAfter(timeout);
        var connected = false;
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            var connectMilliseconds = (int)Math.Clamp(
                Math.Ceiling(timeout.TotalMilliseconds),
                1,
                500);
            await pipe.ConnectAsync(connectMilliseconds, deadline.Token)
                .ConfigureAwait(false);
            connected = true;
            await pipe.WriteAsync(new byte[] { ShutdownRequest }, deadline.Token)
                .ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);

            var response = new byte[1];
            var count = await pipe.ReadAsync(response, deadline.Token)
                .ConfigureAwait(false);
            if (count == 0)
            {
                return new ViewerShutdownResult(
                    ViewerShutdownStatus.ProtocolUnsupported);
            }

            if (response[0] == ShutdownRejected)
            {
                return new ViewerShutdownResult(ViewerShutdownStatus.Rejected);
            }

            if (response[0] != ShutdownAccepted)
            {
                return new ViewerShutdownResult(
                    ViewerShutdownStatus.ProtocolUnsupported);
            }

            while (!deadline.IsCancellationRequested)
            {
                if (!_processProbe() && !IsSingleInstanceMutexHeld(_mutexName))
                {
                    return new ViewerShutdownResult(ViewerShutdownStatus.Stopped);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token)
                    .ConfigureAwait(false);
            }

            return new ViewerShutdownResult(ViewerShutdownStatus.TimedOut);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ViewerShutdownResult(
                connected
                    ? ViewerShutdownStatus.TimedOut
                    : ViewerShutdownStatus.Unavailable);
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or UnauthorizedAccessException)
        {
            if (connected)
            {
                return new ViewerShutdownResult(
                    ViewerShutdownStatus.ProtocolUnsupported);
            }

            return await ProbeLegacyPipeAsync(
                    deadline.Token,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    internal static bool IsViewerProcessRunning()
    {
        var currentSessionId = Process.GetCurrentProcess().SessionId;
        var processes = Process.GetProcessesByName(
            Path.GetFileNameWithoutExtension(
                ViewerSetupConstants.ViewerExecutableName));
        try
        {
            return processes.Any(process =>
            {
                try
                {
                    return !process.HasExited &&
                           IsCurrentSession(process.SessionId, currentSessionId);
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            });
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    internal static bool IsCurrentSession(
        int candidateSessionId,
        int currentSessionId) =>
        candidateSessionId == currentSessionId;

    internal static bool IsSingleInstanceMutexHeld(string mutexName)
    {
        Mutex? mutex = null;
        var acquired = false;
        try
        {
            mutex = new Mutex(initiallyOwned: false, mutexName);
            try
            {
                acquired = mutex.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }

            return !acquired;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        finally
        {
            if (acquired)
            {
                try
                {
                    mutex?.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                }
            }

            mutex?.Dispose();
        }
    }

    private async Task<ViewerShutdownResult> ProbeLegacyPipeAsync(
        CancellationToken deadlineToken,
        CancellationToken callerCancellationToken)
    {
        try
        {
            using var probeDeadline =
                CancellationTokenSource.CreateLinkedTokenSource(deadlineToken);
            probeDeadline.CancelAfter(TimeSpan.FromMilliseconds(500));
            using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(probeDeadline.Token).ConfigureAwait(false);
            await pipe.WriteAsync(
                    new byte[] { ActivationRequest },
                    probeDeadline.Token)
                .ConfigureAwait(false);
            await pipe.FlushAsync(probeDeadline.Token).ConfigureAwait(false);
            return new ViewerShutdownResult(
                ViewerShutdownStatus.ProtocolUnsupported);
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or IOException or
                TimeoutException or UnauthorizedAccessException)
        {
            return new ViewerShutdownResult(ViewerShutdownStatus.Unavailable);
        }
    }
}
