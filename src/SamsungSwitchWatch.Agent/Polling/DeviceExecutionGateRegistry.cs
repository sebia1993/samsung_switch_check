using System.Collections.Concurrent;
using System.Diagnostics;
using SamsungSwitchWatch.Agent.Configuration;

namespace SamsungSwitchWatch.Agent.Polling;

/// <summary>
/// Coordinates scheduled checks, registered manual checks, and ad-hoc read-only
/// queries. A device never receives overlapping Telnet sessions and the Agent's
/// configured cross-device concurrency ceiling is shared by every execution path.
/// </summary>
public sealed class DeviceExecutionGateRegistry
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _deviceGates =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _globalGate;

    public DeviceExecutionGateRegistry(AgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _globalGate = new SemaphoreSlim(options.MaxConcurrentDevices, options.MaxConcurrentDevices);
    }

    public async ValueTask<DeviceExecutionLease> AcquireAsync(
        string deviceId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var deviceGate = _deviceGates.GetOrAdd(deviceId, static _ => new SemaphoreSlim(1, 1));
        await deviceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _globalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new DeviceExecutionLease(deviceGate, _globalGate);
        }
        catch
        {
            deviceGate.Release();
            throw;
        }
    }

    public async ValueTask<DeviceExecutionLease?> TryAcquireAsync(
        string deviceId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        var deviceGate = _deviceGates.GetOrAdd(deviceId, static _ => new SemaphoreSlim(1, 1));
        var elapsed = Stopwatch.StartNew();
        if (!await deviceGate.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        try
        {
            var remaining = timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero ||
                !await _globalGate.WaitAsync(remaining, cancellationToken).ConfigureAwait(false))
            {
                deviceGate.Release();
                return null;
            }

            return new DeviceExecutionLease(deviceGate, _globalGate);
        }
        catch
        {
            deviceGate.Release();
            throw;
        }
    }
}

public sealed class DeviceExecutionLease : IAsyncDisposable
{
    private SemaphoreSlim? _deviceGate;
    private SemaphoreSlim? _globalGate;

    internal DeviceExecutionLease(SemaphoreSlim deviceGate, SemaphoreSlim globalGate)
    {
        _deviceGate = deviceGate;
        _globalGate = globalGate;
    }

    public ValueTask DisposeAsync()
    {
        var globalGate = Interlocked.Exchange(ref _globalGate, null);
        var deviceGate = Interlocked.Exchange(ref _deviceGate, null);
        globalGate?.Release();
        deviceGate?.Release();
        return ValueTask.CompletedTask;
    }
}
