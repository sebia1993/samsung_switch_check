using System.IO.Pipes;

namespace SamsungSwitchWatch.Viewer.Services;

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    private const string MutexName = "Local\\SamsungSwitchWatch.Viewer.Singleton";
    private const string PipeName = "SamsungSwitchWatch.Viewer.Activation";
    private readonly CancellationTokenSource _lifetime = new();
    private Mutex? _mutex;
    private Task? _server;
    private bool _ownsMutex;

    public event EventHandler? ActivationRequested;

    public bool TryAcquire()
    {
        _mutex = new Mutex(false, MutexName);
        try { _ownsMutex = _mutex.WaitOne(0, false); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
        if (_ownsMutex) _server = Task.Run(() => ListenAsync(_lifetime.Token));
        return _ownsMutex;
    }

    public static async Task NotifyExistingAsync(CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
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
                    PipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
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
        _lifetime.Cancel();
        if (_server is not null)
        {
            try { await _server.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
        }
        _mutex?.Dispose();
        _lifetime.Dispose();
    }
}
