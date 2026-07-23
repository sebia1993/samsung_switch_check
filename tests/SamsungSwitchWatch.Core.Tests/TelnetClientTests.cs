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

        Assert.Equal(ErrorCodes.TelnetSessionClosed, exception.Error.Code);
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
    public async Task ExecuteAsync_ElevatesFromUserPromptBeforeRunningShowCommand()
    {
        var transport = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01>"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"),
            Bytes("show running-config\r\nsynthetic configuration\r\nACCESS-SW-01#"));
        var client = CreateClient(transport);

        var result = await client.ExecuteAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("operator", "login-secret", "enable-secret"),
            Ies4224GpProfile.Create().Telnet,
            ["show running-config"]);

        Assert.Equal(TelnetPrivilege.Privileged, result.Privilege);
        Assert.Equal('#', result.PromptTerminator);
        Assert.Equal(
            "synthetic configuration",
            Assert.Single(result.Outputs).NormalizedOutput);
        Assert.Contains(transport.Writes, write =>
            Encoding.ASCII.GetString(write) == "enable\r\n");
        Assert.Contains(transport.Writes, write =>
            Encoding.ASCII.GetString(write) == "enable-secret\r\n");
        Assert.True(transport.WasClosed);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsEnableWhenLoginAlreadyReturnsPrivilegedPrompt()
    {
        var transport = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"),
            Bytes("show system\r\nUptime: 1 day\r\nACCESS-SW-01#"));
        var client = CreateClient(transport);

        var result = await client.ExecuteAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("operator", "login-secret", "unused-enable-secret"),
            Ies4224GpProfile.Create().Telnet,
            ["show system"]);

        Assert.Equal(TelnetPrivilege.Privileged, result.Privilege);
        Assert.DoesNotContain(transport.Writes, write =>
            Encoding.ASCII.GetString(write) == "enable\r\n");
        Assert.DoesNotContain(transport.Writes, write =>
            Encoding.ASCII.GetString(write).Contains("unused-enable-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_LeavesUserPrivilegeWhenEnablePasswordIsOmitted()
    {
        var transport = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01>"),
            Bytes("show port status\r\n1 Up\r\nACCESS-SW-01>"));
        var client = CreateClient(transport);

        var result = await client.ExecuteAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("operator", "login-secret"),
            Ies4224GpProfile.Create().Telnet,
            ["show port status"]);

        Assert.Equal(TelnetPrivilege.User, result.Privilege);
        Assert.Equal('>', result.PromptTerminator);
        Assert.DoesNotContain(transport.Writes, write =>
            Encoding.ASCII.GetString(write) == "enable\r\n");
    }

    [Fact]
    public async Task ExecuteAsync_MapsRejectedEnableWithoutLeakingSecret()
    {
        var transport = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01>"),
            Bytes("Password:"),
            Bytes("Authentication failed\r\nACCESS-SW-01>"));
        var client = CreateClient(transport);

        var exception = await Assert.ThrowsAsync<SwitchWatchException>(() => client.ExecuteAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("operator", "login-secret", "enable-secret"),
            Ies4224GpProfile.Create().Telnet,
            ["show system"]));

        Assert.Equal(ErrorCodes.EnableFailed, exception.Error.Code);
        Assert.DoesNotContain("enable-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.True(transport.WasClosed);
    }

    [Fact]
    public async Task ExecuteAsync_TestSessionLogsOutWithoutRunningACommand()
    {
        var transport = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"));
        var client = CreateClient(transport);

        var result = await client.ExecuteAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("operator", "login-secret"),
            Ies4224GpProfile.Create().Telnet,
            []);

        Assert.Empty(result.Outputs);
        Assert.Contains(transport.Writes, write =>
            Encoding.ASCII.GetString(write) == "exit\r\n");
        Assert.True(transport.WasClosed);
    }

    [Fact]
    public async Task ExecuteAsync_RetriesOnlyRemainingShowCommandsAfterRemoteClose()
    {
        var first = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"),
            Bytes("show port status\r\n1 Up\r\nACCESS-SW-01#"));
        var second = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"),
            Bytes("show sylog tail num 100\r\nsynthetic log\r\nACCESS-SW-01#"));
        var factory = new SequenceTransportFactory(first, second);
        var client = CreateResilientClient(
            factory,
            TimeSpan.FromSeconds(5),
            retryCount: 1);

        var result = await client.ExecuteAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("operator", "login-secret"),
            Ies4224GpProfile.Create().Telnet,
            ["show port status", "show sylog tail num 100"]);

        Assert.Equal(
            ["show port status", "show sylog tail num 100"],
            result.Outputs.Select(output => output.Command));
        Assert.Equal(2, result.SessionCount);
        Assert.Equal(1, result.ReconnectCount);
        Assert.DoesNotContain(second.Writes, write =>
            Encoding.ASCII.GetString(write).Contains("show port status", StringComparison.Ordinal));
        Assert.Contains(second.Writes, write =>
            Encoding.ASCII.GetString(write).Contains("show sylog tail num 100", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryRemoteCloseDuringAuthentication()
    {
        var first = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"));
        var unusedSecond = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"));
        var factory = new SequenceTransportFactory(first, unusedSecond);
        var client = CreateResilientClient(
            factory,
            TimeSpan.FromSeconds(2),
            retryCount: 1);

        var exception = await Assert.ThrowsAsync<SwitchWatchException>(() => client.ExecuteAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("operator", "login-secret"),
            Ies4224GpProfile.Create().Telnet,
            ["show port status"]));

        Assert.Equal(ErrorCodes.TelnetSessionClosed, exception.Error.Code);
        Assert.Equal("authentication", exception.Error.Stage);
        Assert.Equal(1, factory.CreateCalls);
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

    [Fact]
    public async Task ExecuteRegisteredAsync_RetriesOnlyRemainingCommandsAfterRemoteClose()
    {
        var first = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"),
            Bytes("show version\r\nModel Name : IES4224GP\r\nACCESS-SW-01#"));
        var second = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"),
            Bytes("show system\r\nUptime: 0 days, 01:00:00\r\nACCESS-SW-01#"));
        var factory = new SequenceTransportFactory(first, second);
        var profile = TwoCommandProfile(TimeSpan.FromSeconds(1));
        var client = CreateResilientClient(factory, TimeSpan.FromSeconds(5), retryCount: 1);

        var result = await client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            profile,
            [CommandIds.Version, CommandIds.System]);

        Assert.Equal([CommandIds.Version, CommandIds.System], result.Outputs.Select(output => output.CommandId));
        Assert.Equal(2, result.SessionCount);
        Assert.Equal(1, result.ReconnectCount);
        Assert.Contains(first.Writes, write =>
            Encoding.ASCII.GetString(write).Contains("show system", StringComparison.Ordinal));
        Assert.Contains(second.Writes, write =>
            Encoding.ASCII.GetString(write).Contains("show system", StringComparison.Ordinal));
        Assert.DoesNotContain(second.Writes, write =>
            Encoding.ASCII.GetString(write).Contains("show version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_ReportsPartialResultsWhenReconnectAlsoCloses()
    {
        var first = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"),
            Bytes("show version\r\nModel Name : IES4224GP\r\nACCESS-SW-01#"));
        var second = new ScriptedTransport();
        var client = CreateResilientClient(
            new SequenceTransportFactory(first, second),
            TimeSpan.FromSeconds(5),
            retryCount: 1);

        var exception = await Assert.ThrowsAsync<TelnetExecutionException>(() => client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            TwoCommandProfile(TimeSpan.FromSeconds(1)),
            [CommandIds.Version, CommandIds.System]));

        Assert.Equal(ErrorCodes.TelnetSessionClosed, exception.Error.Code);
        Assert.Equal(CommandIds.Version, Assert.Single(exception.CompletedOutputs).CommandId);
        Assert.Equal([CommandIds.System], exception.RemainingCommandIds);
        Assert.Equal(2, exception.SessionCount);
        Assert.Equal(1, exception.ReconnectCount);
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_DoesNotImmediatelyRetryCommandTimeout()
    {
        var transport = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"))
        {
            WaitWhenExhausted = true
        };
        var factory = new SequenceTransportFactory(transport, new ScriptedTransport());
        var client = CreateResilientClient(factory, TimeSpan.FromSeconds(2), retryCount: 1);
        var profile = new DeviceCommandProfile(
            Ies4224GpProfile.Create().Model,
            Ies4224GpProfile.Create().Telnet,
            [new ReadOnlyCommandDefinition(CommandIds.System, "System", "show system", TimeSpan.FromMilliseconds(30), 60)]);

        var exception = await Assert.ThrowsAsync<SwitchWatchException>(() => client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            profile,
            [CommandIds.System]));

        Assert.Equal(ErrorCodes.CommandTimeout, exception.Error.Code);
        Assert.Equal(1, factory.CreateCalls);
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_DoesNotRetrySessionCloseDuringAuthentication()
    {
        var first = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"));
        var unusedSecond = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"));
        var factory = new SequenceTransportFactory(first, unusedSecond);
        var client = CreateResilientClient(factory, TimeSpan.FromSeconds(2), retryCount: 1);

        var exception = await Assert.ThrowsAsync<SwitchWatchException>(() => client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            Ies4224GpProfile.Create(),
            [CommandIds.System]));

        Assert.Equal(ErrorCodes.TelnetSessionClosed, exception.Error.Code);
        Assert.Equal("authentication", exception.Error.Stage);
        Assert.Equal(1, factory.CreateCalls);
        Assert.Single(first.Writes, write =>
            Encoding.ASCII.GetString(write).Contains("monitor", StringComparison.Ordinal));
        Assert.Single(first.Writes, write =>
            Encoding.ASCII.GetString(write).Contains("synthetic-password", StringComparison.Ordinal));
        Assert.Empty(unusedSecond.Writes);
    }

    [Fact]
    public async Task ExecuteRegisteredAsync_SplitsCommandsWhenCalculatedBudgetExceedsMaximum()
    {
        var first = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"),
            Bytes("show version\r\nModel Name : IES4224GP\r\nACCESS-SW-01#"));
        var second = new ScriptedTransport(
            Bytes("Login:"),
            Bytes("Password:"),
            Bytes("ACCESS-SW-01#"),
            Bytes("show system\r\nUptime: 0 days, 01:00:00\r\nACCESS-SW-01#"));
        var factory = new SequenceTransportFactory(first, second);
        var client = CreateResilientClient(factory, TimeSpan.FromMilliseconds(160), retryCount: 0);

        var result = await client.ExecuteRegisteredAsync(
            new TelnetEndpoint("192.0.2.10"),
            new TelnetCredentials("monitor", "synthetic-password"),
            TwoCommandProfile(TimeSpan.FromMilliseconds(100)),
            [CommandIds.Version, CommandIds.System]);

        Assert.Equal(2, result.SessionCount);
        Assert.Equal(0, result.ReconnectCount);
        Assert.Equal(2, factory.CreateCalls);
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
            new TelnetClientOptions(timeouts, 2 * 1024 * 1024, 512, maximumNegotiationBytes, maximumWireBytes)
            {
                SessionCloseRetryCount = 0
            });
    }

    private static TelnetClient CreateResilientClient(
        IByteTransportFactory factory,
        TimeSpan maximumSession,
        int retryCount)
    {
        var timeouts = new TelnetTimeouts(
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(10))
        {
            Write = TimeSpan.FromMilliseconds(50),
            Session = maximumSession
        };
        return new TelnetClient(factory, new TelnetClientOptions(timeouts, ReadBufferBytes: 512)
        {
            SessionSafetyMargin = TimeSpan.FromMilliseconds(10),
            SessionCloseRetryCount = retryCount,
            SessionCloseRetryDelay = TimeSpan.FromMilliseconds(1)
        });
    }

    private static DeviceCommandProfile TwoCommandProfile(TimeSpan commandTimeout)
    {
        var baseProfile = Ies4224GpProfile.Create();
        return new DeviceCommandProfile(baseProfile.Model, baseProfile.Telnet,
        [
            new ReadOnlyCommandDefinition(CommandIds.Version, "Version", "show version", commandTimeout, 60),
            new ReadOnlyCommandDefinition(CommandIds.System, "System", "show system", commandTimeout, 60)
        ]);
    }

    private static byte[] Bytes(string value) => Encoding.ASCII.GetBytes(value);

    private sealed class FixedTransportFactory(IByteTransport transport) : IByteTransportFactory
    {
        public IByteTransport Create() => transport;
    }

    private sealed class SequenceTransportFactory(params IByteTransport[] transports) : IByteTransportFactory
    {
        private readonly Queue<IByteTransport> _transports = new(transports);

        public int CreateCalls { get; private set; }

        public IByteTransport Create()
        {
            CreateCalls++;
            return _transports.Dequeue();
        }
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
