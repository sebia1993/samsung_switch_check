using System.Text;
using SamsungSwitchWatch.Core.Diagnostics;
using SamsungSwitchWatch.Core.Profiles;
using SamsungSwitchWatch.Core.Telnet;
using SamsungSwitchWatch.Core.Transport;

namespace SamsungSwitchWatch.Core.Tests;

public sealed class TelnetClientTests
{
    [Theory]
    [InlineData("operator\rshow system", "secret")]
    [InlineData("operator\nshow system", "secret")]
    [InlineData("operator\0suffix", "secret")]
    [InlineData("operator", "secret\rnext")]
    [InlineData("operator", "secret\nnext")]
    [InlineData("operator", "secret\0suffix")]
    public void TelnetCredentials_RejectLineAndNulInjection(string username, string password)
    {
        Assert.Throws<ArgumentException>(() => new TelnetCredentials(username, password));
    }

    [Fact]
    public void TelnetCredentials_RejectOversizedFields()
    {
        Assert.Throws<ArgumentException>(() => new TelnetCredentials(new string('u', 129), "secret"));
        Assert.Throws<ArgumentException>(() => new TelnetCredentials("operator", new string('p', 513)));
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_HandlesIacPagingAndNormalizesOutput()
    {
        var loginWithIac = new byte[] { 255, 251, 1 }
            .Concat(Encoding.ASCII.GetBytes("Username:"))
            .ToArray();
        var transport = new ScriptedTransport(
            loginWithIac,
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"),
            Bytes("show interfaces status\r\nPort Admin Link Speed Duplex\r\n1 Enabled Up 1000M Full\r\n--More--"),
            Bytes("24 Enabled Down -- --\r\nACCESS-SW-01#"));
        var client = CreateClient(transport);

        var result = await client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            Ies4224GpProfile.Create(),
            [CommandIds.InterfaceStatus]);

        var output = Assert.Single(result.Outputs);
        Assert.Contains("--More--", output.RawOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("--More--", output.NormalizedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("ACCESS-SW-01#", output.NormalizedOutput, StringComparison.Ordinal);
        Assert.Contains("24 Enabled Down", output.NormalizedOutput, StringComparison.Ordinal);
        Assert.Contains(transport.Writes, bytes => bytes.SequenceEqual(new byte[] { 255, 254, 1 }));
        Assert.Contains(transport.Writes, bytes => bytes.SequenceEqual(new byte[] { 0x20 }));
        Assert.True(transport.WasClosed);
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_UsesCapturedPromptInsteadOfHashEndedOutputLine()
    {
        var transport = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"),
            Bytes("show system\r\nAudit summary #"),
            Bytes("\r\nUptime: 0 days, 01:00:00\r\nACCESS-SW-01#"));
        var client = CreateClient(transport);

        var result = await client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            Ies4224GpProfile.Create(),
            [CommandIds.System]);

        var output = Assert.Single(result.Outputs);
        Assert.Contains("Audit summary #", output.NormalizedOutput, StringComparison.Ordinal);
        Assert.Contains("Uptime: 0 days, 01:00:00", output.NormalizedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("ACCESS-SW-01#", output.NormalizedOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_RecognizesCapturedPromptAcrossReads()
    {
        var transport = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"),
            Bytes("show system\r\nUptime: 0 days, 01:00:00\r\nACCESS-"),
            Bytes("SW-01#"));
        var client = CreateClient(transport);

        var result = await client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            Ies4224GpProfile.Create(),
            [CommandIds.System]);

        Assert.Contains(
            "Uptime: 0 days, 01:00:00",
            Assert.Single(result.Outputs).NormalizedOutput,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_DoesNotTreatEmbeddedPagingTextAsMarker()
    {
        var transport = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"),
            Bytes("show log ram\r\nWarning: Press any key to continue policy changed\r\nACCESS-SW-01#"));
        var client = CreateClient(transport);

        var result = await client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            Ies4224GpProfile.Create(),
            [CommandIds.LogRam]);

        Assert.Contains(
            "Warning: Press any key to continue policy changed",
            Assert.Single(result.Outputs).NormalizedOutput,
            StringComparison.Ordinal);
        Assert.DoesNotContain(transport.Writes, bytes => bytes.SequenceEqual(new byte[] { 0x20 }));
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_HandlesWhitespacePaddedFullLinePagingMarker()
    {
        var transport = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"),
            Bytes("show interfaces status\r\n1 Enabled Up 1000M Full\r\n  Press SPACE to continue  \r\n"),
            Bytes("24 Enabled Down -- --\r\nACCESS-SW-01#"));
        var client = CreateClient(transport);

        var result = await client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            Ies4224GpProfile.Create(),
            [CommandIds.InterfaceStatus]);

        var output = Assert.Single(result.Outputs);
        Assert.Contains("1 Enabled Up", output.NormalizedOutput, StringComparison.Ordinal);
        Assert.Contains("24 Enabled Down", output.NormalizedOutput, StringComparison.Ordinal);
        Assert.DoesNotContain("Press SPACE", output.NormalizedOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(transport.Writes, bytes => bytes.SequenceEqual(new byte[] { 0x20 }));
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_DoesNotReturnPartialOutputWhenPromptIsMissingAfterPaging()
    {
        var transport = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"),
            Bytes("show interfaces status\r\n1 Enabled Up 1000M Full\r\n--More--"));
        var client = CreateClient(transport);

        var exception = await Assert.ThrowsAsync<SwitchWatchException>(() => client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            Ies4224GpProfile.Create(),
            [CommandIds.InterfaceStatus]));

        Assert.Equal(ErrorCodes.PromptParseFailed, exception.Error.Code);
        Assert.Equal("command", exception.Error.Stage);
        Assert.Contains(transport.Writes, bytes => bytes.SequenceEqual(new byte[] { 0x20 }));
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_MapsTcpTimeoutToStableCode()
    {
        var transport = new ScriptedTransport { WaitDuringConnect = true };
        var client = CreateClient(transport, connect: TimeSpan.FromMilliseconds(30));

        var exception = await Assert.ThrowsAsync<SwitchWatchException>(() => client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            Ies4224GpProfile.Create(),
            [CommandIds.System]));

        Assert.Equal(ErrorCodes.TcpTimeout, exception.Error.Code);
        Assert.Equal("tcp-connect", exception.Error.Stage);
        Assert.DoesNotContain("192.0.2.10", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_MapsMissingLoginPromptToStableCode()
    {
        var transport = new ScriptedTransport { WaitWhenExhausted = true };
        var client = CreateClient(transport, login: TimeSpan.FromMilliseconds(30));

        var exception = await Assert.ThrowsAsync<SwitchWatchException>(() => client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            Ies4224GpProfile.Create(),
            [CommandIds.System]));

        Assert.Equal(ErrorCodes.LoginPromptNotFound, exception.Error.Code);
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_MapsExplicitAuthenticationFailureWithoutLeakingCredentials()
    {
        var transport = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("Authentication failed\r\nLogin:"));
        var client = CreateClient(transport);

        var exception = await Assert.ThrowsAsync<SwitchWatchException>(() => client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("private-user", "private-password"),
            Ies4224GpProfile.Create(),
            [CommandIds.System]));

        Assert.Equal(ErrorCodes.AuthFailed, exception.Error.Code);
        Assert.DoesNotContain("private-user", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-password", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_MapsCommandTimeoutToStableCode()
    {
        var transport = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"))
        {
            WaitWhenExhausted = true
        };
        var baseProfile = Ies4224GpProfile.Create();
        var fastProfile = new DeviceCommandProfile(
            baseProfile.Model,
            baseProfile.Telnet,
            [new ReadOnlyCommandDefinition(CommandIds.System, "System", "show system", TimeSpan.FromMilliseconds(30), 60)]);
        var client = CreateClient(transport);

        var exception = await Assert.ThrowsAsync<SwitchWatchException>(() => client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            fastProfile,
            [CommandIds.System]));

        Assert.Equal(ErrorCodes.CommandTimeout, exception.Error.Code);
        Assert.Equal("command", exception.Error.Stage);
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_RejectsUnregisteredCommandIdBeforeConnecting()
    {
        var transport = new ScriptedTransport();
        var client = CreateClient(transport);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            Ies4224GpProfile.Create(),
            ["configure-terminal"]));

        Assert.False(transport.ConnectWasCalled);
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_MapsNegotiationControlFloodToStableCode()
    {
        var transport = new ScriptedTransport(new byte[] { 255, 241, 255, 241, 255, 241 });
        var client = CreateClient(transport, maximumNegotiationBytes: 3);

        var exception = await Assert.ThrowsAsync<SwitchWatchException>(() => client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            Ies4224GpProfile.Create(),
            [CommandIds.System]));

        Assert.Equal(ErrorCodes.TelnetNegotiationFailed, exception.Error.Code);
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_TimesOutAStuckCommandWrite()
    {
        var transport = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"))
        {
            WaitOnWriteNumber = 3
        };
        var client = CreateClient(transport, write: TimeSpan.FromMilliseconds(30));

        var exception = await Assert.ThrowsAsync<SwitchWatchException>(() => client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            Ies4224GpProfile.Create(),
            [CommandIds.System]));

        Assert.Equal(ErrorCodes.CommandTimeout, exception.Error.Code);
        Assert.Equal("command-write", exception.Error.Stage);
        Assert.True(transport.WasClosed);
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_LimitsWireBytesAsWellAsVisibleText()
    {
        var transport = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"),
            Bytes("show system\r\n" + new string('x', 1100)));
        var client = CreateClient(transport, maximumWireBytes: 1024);

        var exception = await Assert.ThrowsAsync<SwitchWatchException>(() => client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            Ies4224GpProfile.Create(),
            [CommandIds.System]));

        Assert.Equal(ErrorCodes.OutputLimitExceeded, exception.Error.Code);
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_RejectsIpv6BeforeConnecting()
    {
        var transport = new ScriptedTransport();
        var client = CreateClient(transport);

        var exception = await Assert.ThrowsAsync<SwitchWatchException>(() => client.ExecuteRegisteredAsync(
            new TelnetEndpoint("2001:db8::10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            Ies4224GpProfile.Create(),
            [CommandIds.System]));

        Assert.Equal(ErrorCodes.Ipv6Unsupported, exception.Error.Code);
        Assert.False(transport.ConnectWasCalled);
    }

    [Fact]
    public void DeviceCommandProfile_RejectsControlCharactersAndSeparators()
    {
        var baseProfile = Ies4224GpProfile.Create();

        Assert.Throws<ArgumentException>(() => new DeviceCommandProfile(
            baseProfile.Model,
            baseProfile.Telnet,
            [new ReadOnlyCommandDefinition("unsafe", "Unsafe", "show system\nreload", TimeSpan.FromSeconds(1), 60)]));
        Assert.Throws<ArgumentException>(() => new DeviceCommandProfile(
            baseProfile.Model,
            baseProfile.Telnet,
            [new ReadOnlyCommandDefinition("unsafe", "Unsafe", "show system; reload", TimeSpan.FromSeconds(1), 60)]));
    }

    private static TelnetClient CreateClient(
        ScriptedTransport transport,
        TimeSpan? connect = null,
        TimeSpan? login = null,
        int maximumNegotiationBytes = 16 * 1024,
        TimeSpan? write = null,
        int maximumWireBytes = 2 * 1024 * 1024)
    {
        var timeouts = new TelnetTimeouts(
            connect ?? TimeSpan.FromSeconds(1),
            login ?? TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(50))
        {
            Write = write ?? TimeSpan.FromSeconds(1),
            Session = TimeSpan.FromSeconds(5)
        };
        return new TelnetClient(
            new FixedTransportFactory(transport),
            new TelnetClientOptions(timeouts, 2 * 1024 * 1024, 512, maximumNegotiationBytes, maximumWireBytes));
    }

    private static byte[] Bytes(string value) => Encoding.ASCII.GetBytes(value);

    private sealed class FixedTransportFactory(IByteTransport transport) : IByteTransportFactory
    {
        public IByteTransport Create() => transport;
    }

    private sealed class ScriptedTransport : IByteTransport
    {
        private readonly Queue<byte[]> _reads = [];
        private byte[]? _current;
        private int _offset;

        public ScriptedTransport(params byte[][] reads)
        {
            foreach (var read in reads)
            {
                _reads.Enqueue(read);
            }
        }

        public bool WaitDuringConnect { get; init; }

        public bool WaitWhenExhausted { get; init; }

        public int? WaitOnWriteNumber { get; init; }

        public bool ConnectWasCalled { get; private set; }

        public bool IsConnected { get; private set; }

        public bool WasClosed { get; private set; }

        public List<byte[]> Writes { get; } = [];

        private int WriteCount { get; set; }

        public async ValueTask ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            ConnectWasCalled = true;
            if (WaitDuringConnect)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            IsConnected = true;
        }

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (_current is null || _offset >= _current.Length)
            {
                if (_reads.Count == 0)
                {
                    if (WaitWhenExhausted)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    }

                    return 0;
                }

                _current = _reads.Dequeue();
                _offset = 0;
            }

            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            if (WriteCount == WaitOnWriteNumber)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            Writes.Add(buffer.ToArray());
        }

        public ValueTask CloseAsync()
        {
            IsConnected = false;
            WasClosed = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => CloseAsync();
    }
}
