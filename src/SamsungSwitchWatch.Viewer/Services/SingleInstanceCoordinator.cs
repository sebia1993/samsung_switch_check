using System.IO.Pipes;

namespace SamsungSwitchWatch.Viewer.Services;

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    // Single-instance pipe wire contract:
    // 0x01 remains the legacy one-way activation request.
    // 0x02 is a shutdown request; protocol-aware Viewers reply 0x81 when the
    // safe application exit path was queued or 0x82 when no handler exists.
    // Legacy input-only Viewers cannot return either response, allowing Setup
    // to fall back to manual-close guidance without forcing process shutdown.
    private const byte ActivationRequest = 0x01;
    private const byte ShutdownRequest = 0x02;
    private const byte ShutdownAcceptedResponse = 0x81;
    private const byte ShutdownRejectedResponse = 0x82;
    private const string MutexName = "Local\\SamsungSwitchWatch.Viewer.Singleton";
    private const string PipeName = "SamsungSwitchWatch.Viewer.Activation";
    private static readonly TimeSpan PipeResponseTimeout = TimeSpan.FromSeconds(1);
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
    public event EventHandler? ShutdownRequested;

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
                await pipe.WriteAsync(
                        new byte[] { ActivationRequest },
                        cancellationToken)
                    .ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is TimeoutException or IOException)
            {
                if (attempt < 2) await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<SingleInstanceShutdownRequestResult> RequestShutdownAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? pipeName = null)
    {
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                "The shutdown request timeout must be positive.");
        }

        var targetPipeName = pipeName is null ? PipeName : RequireName(pipeName);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var connected = false;
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                targetPipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            var connectTimeoutMilliseconds = (int)Math.Clamp(
                Math.Ceiling(timeout.TotalMilliseconds),
                1,
                300);
            await pipe.ConnectAsync(connectTimeoutMilliseconds, deadline.Token)
                .ConfigureAwait(false);
            connected = true;
            await pipe.WriteAsync(
                    new byte[] { ShutdownRequest },
                    deadline.Token)
                .ConfigureAwait(false);
            await pipe.FlushAsync(deadline.Token).ConfigureAwait(false);

            var response = new byte[1];
            var count = await pipe.ReadAsync(response, deadline.Token).ConfigureAwait(false);
            if (count == 0)
            {
                return SingleInstanceShutdownRequestResult.ProtocolUnsupported;
            }

            return response[0] switch
            {
                ShutdownAcceptedResponse => SingleInstanceShutdownRequestResult.Accepted,
                ShutdownRejectedResponse => SingleInstanceShutdownRequestResult.Rejected,
                _ => SingleInstanceShutdownRequestResult.ProtocolUnsupported
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return connected
                ? SingleInstanceShutdownRequestResult.ResponseTimedOut
                : SingleInstanceShutdownRequestResult.Unavailable;
        }
        catch (UnauthorizedAccessException)
        {
            return await ProbeLegacyActivationPipeAsync(
                    targetPipeName,
                    deadline.Token,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is TimeoutException or IOException)
        {
            if (connected)
            {
                return SingleInstanceShutdownRequestResult.ProtocolUnsupported;
            }

            return await ProbeLegacyActivationPipeAsync(
                    targetPipeName,
                    deadline.Token,
                    cancellationToken)
                .ConfigureAwait(false);
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
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var buffer = new byte[1];
                if (await pipe.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
                {
                    if (buffer[0] == ActivationRequest)
                    {
                        ActivationRequested?.Invoke(this, EventArgs.Empty);
                    }
                    else if (buffer[0] == ShutdownRequest)
                    {
                        var accepted = ShutdownRequested is not null;
                        await TryWriteResponseAsync(
                                pipe,
                                accepted
                                    ? ShutdownAcceptedResponse
                                    : ShutdownRejectedResponse,
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (accepted)
                        {
                            try { ShutdownRequested?.Invoke(this, EventArgs.Empty); }
                            catch { }
                        }
                    }
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

    private static async Task<SingleInstanceShutdownRequestResult> ProbeLegacyActivationPipeAsync(
        string pipeName,
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
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(probeDeadline.Token).ConfigureAwait(false);
            await pipe.WriteAsync(
                    new byte[] { ActivationRequest },
                    probeDeadline.Token)
                .ConfigureAwait(false);
            await pipe.FlushAsync(probeDeadline.Token).ConfigureAwait(false);
            return SingleInstanceShutdownRequestResult.ProtocolUnsupported;
        }
        catch (OperationCanceledException) when (callerCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or TimeoutException or
                IOException or UnauthorizedAccessException)
        {
            return SingleInstanceShutdownRequestResult.Unavailable;
        }
    }

    private static async Task TryWriteResponseAsync(
        NamedPipeServerStream pipe,
        byte response,
        CancellationToken cancellationToken)
    {
        using var responseDeadline =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        responseDeadline.CancelAfter(PipeResponseTimeout);
        try
        {
            await pipe.WriteAsync(new byte[] { response }, responseDeadline.Token)
                .ConfigureAwait(false);
            await pipe.FlushAsync(responseDeadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or IOException)
        {
            // The shutdown request was received. A disconnected requester must
            // not keep the Viewer alive after it has already asked to exit.
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

internal enum SingleInstanceShutdownRequestResult
{
    Accepted,
    Rejected,
    ProtocolUnsupported,
    Unavailable,
    ResponseTimedOut
}
