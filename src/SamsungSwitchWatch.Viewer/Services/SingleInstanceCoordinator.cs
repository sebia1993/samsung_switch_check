using System.IO.Pipes;

namespace SamsungSwitchWatch.Viewer.Services;

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    private const string MutexName = "Local\\SamsungSwitchWatch.Viewer.Singleton";
    private const string PipeName = "SamsungSwitchWatch.Viewer.Activation";
    private static readonly string VersionMutexName =
        $"Local\\SamsungSwitchWatch.Viewer.{BuildVersionMutexToken()}.Singleton";
    private readonly string _mutexName;
    private readonly string _versionMutexName;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetime = new();
    private Mutex? _mutex;
    private Mutex? _versionMutex;
    private Task? _server;
    private bool _ownsMutex;
    private bool _ownsVersionMutex;

    public event EventHandler? ActivationRequested;

    public SingleInstanceCoordinator()
        : this(MutexName, VersionMutexName, PipeName)
    {
    }

    internal SingleInstanceCoordinator(
        string mutexName,
        string versionMutexName,
        string pipeName)
    {
        _mutexName = RequireName(mutexName);
        _versionMutexName = RequireName(versionMutexName);
        _pipeName = RequireName(pipeName);
    }

    internal SingleInstanceAcquireResult TryAcquire()
    {
        _versionMutex = new Mutex(false, _versionMutexName);
        try { _ownsVersionMutex = _versionMutex.WaitOne(0, false); }
        catch (AbandonedMutexException) { _ownsVersionMutex = true; }
        if (!_ownsVersionMutex)
        {
            return SingleInstanceAcquireResult.CurrentVersionAlreadyRunning;
        }

        _mutex = new Mutex(false, _mutexName);
        try { _ownsMutex = _mutex.WaitOne(0, false); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
        if (!_ownsMutex)
        {
            ReleaseVersionMutex();
            return SingleInstanceAcquireResult.DifferentVersionRunning;
        }

        _server = Task.Run(() => ListenAsync(_lifetime.Token));
        return SingleInstanceAcquireResult.Acquired;
    }

    internal static async Task NotifyExistingAsync(
        CancellationToken cancellationToken = default,
        string? pipeName = null)
    {
        var targetPipeName = pipeName is null ? PipeName : RequireName(pipeName);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".",
                    targetPipeName,
                    PipeDirection.Out,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(300, cancellationToken).ConfigureAwait(false);
                await pipe.WriteAsync(new byte[] { 1 }, cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is TimeoutException or IOException)
            {
                if (attempt < 2) await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ListenAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[1];
                if (await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
                {
                    ActivationRequested?.Invoke(this, EventArgs.Empty);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // A named Mutex must be released by the thread that acquired it. App shutdown enters
        // this method on the UI thread, so release before the first await can move continuation.
        if (_ownsMutex)
        {
            try { _mutex?.ReleaseMutex(); } catch (ApplicationException) { }
            _ownsMutex = false;
        }
        ReleaseVersionMutex();
        _lifetime.Cancel();
        if (_server is not null)
        {
            try { await _server.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
        }
        _mutex?.Dispose();
        _versionMutex?.Dispose();
        _lifetime.Dispose();
    }

    internal static string BuildVersionMutexToken(string? version = null)
    {
        var normalized = AgentProductVersionPolicy.Normalize(
            version ?? AgentProductVersionPolicy.CurrentViewerVersion);
        if (normalized.Length == 0)
        {
            normalized = "unknown";
        }

        return new string(normalized
            .Select(character =>
                char.IsLetterOrDigit(character) || character is '.' or '-' or '_'
                    ? character
                    : '_')
            .ToArray());
    }

    private void ReleaseVersionMutex()
    {
        if (!_ownsVersionMutex)
        {
            return;
        }

        try { _versionMutex?.ReleaseMutex(); } catch (ApplicationException) { }
        _ownsVersionMutex = false;
    }

    private static string RequireName(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("Single-instance object names cannot be empty.")
            : value;
}

internal enum SingleInstanceAcquireResult
{
    Acquired,
    CurrentVersionAlreadyRunning,
    DifferentVersionRunning
}
