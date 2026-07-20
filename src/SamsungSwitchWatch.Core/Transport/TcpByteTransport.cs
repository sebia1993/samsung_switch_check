using System.Net.Sockets;

namespace SamsungSwitchWatch.Core.Transport;

public sealed class TcpByteTransportFactory : IByteTransportFactory
{
    public IByteTransport Create() => new TcpByteTransport();
}

public sealed class TcpByteTransport : IByteTransport
{
    private TcpClient? _client;
    private NetworkStream? _stream;

    public bool IsConnected => _client?.Connected == true && _stream is not null;

    public async ValueTask ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        await CloseAsync().ConfigureAwait(false);
        var client = new TcpClient(AddressFamily.InterNetwork)
        {
            NoDelay = true
        };

        try
        {
            await client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);
            _client = client;
            _stream = client.GetStream();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("The transport is not connected.");
        return stream.ReadAsync(buffer, cancellationToken);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        var stream = _stream ?? throw new InvalidOperationException("The transport is not connected.");
        return stream.WriteAsync(buffer, cancellationToken);
    }

    public ValueTask CloseAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await CloseAsync().ConfigureAwait(false);
}
