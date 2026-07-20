namespace SamsungSwitchWatch.Core.Transport;

/// <summary>
/// Small byte-stream seam used by the Telnet client. Tests can substitute a
/// deterministic transport without ever contacting a real switch.
/// </summary>
public interface IByteTransport : IAsyncDisposable
{
    bool IsConnected { get; }

    ValueTask ConnectAsync(string host, int port, CancellationToken cancellationToken);

    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);

    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);

    ValueTask CloseAsync();
}

public interface IByteTransportFactory
{
    IByteTransport Create();
}
