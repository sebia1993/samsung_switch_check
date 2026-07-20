using SamsungSwitchWatch.Core.Diagnostics;
using SamsungSwitchWatch.Core.Models;
using SamsungSwitchWatch.Core.Parsing;

namespace SamsungSwitchWatch.Core.Tests;

public sealed class ParserTests
{
    [Fact]
    public void VersionParser_ExtractsKnownFields()
    {
        var result = VersionOutputParser.Parse(SyntheticOutputs.Version);

        Assert.True(result.IsSuccess);
        Assert.Equal("IES4224GP", result.Value!.Model);
        Assert.Equal("2.7.1-test", result.Value.SoftwareVersion);
        Assert.Equal("R03", result.Value.HardwareVersion);
        Assert.Equal("normal", result.Value.MainPowerStatus);
    }

    [Fact]
    public void SystemParser_ExtractsUptimeAndPostChecks()
    {
        var result = SystemOutputParser.Parse(SyntheticOutputs.SystemTenHours);

        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromHours(10), result.Value!.Uptime);
        Assert.Equal("PASS", result.Value.PostChecks["Boot POST Check"]);
        Assert.Equal("PASS", result.Value.PostChecks["Memory Test"]);
    }

    [Fact]
    public void UnknownSystemFormat_ReturnsParserUnsupportedWithoutInventingState()
    {
        var result = SystemOutputParser.Parse("vendor-private output with changing clock 12:34:56");

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(ErrorCodes.ParserUnsupported, result.Error!.Code);
    }

    [Fact]
    public void LogParser_PreservesRepeatedMessagesAtDifferentTimes()
    {
        var output = SyntheticOutputs.Logs(
            (10, "14:01:00", "Port 1 link down"),
            (11, "14:02:00", "Port 1 link down"));

        var result = LogOutputParser.Parse(output);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Entries.Count);
        Assert.NotEqual(result.Value.Entries[0].Identity, result.Value.Entries[1].Identity);
        Assert.Equal(10, result.Value.Entries[0].SequenceNumber);
    }

    [Fact]
    public void InterfaceParser_ExtractsLinkStateWithoutGuessingUnknownColumns()
    {
        var result = InterfaceStatusOutputParser.Parse(SyntheticOutputs.InterfacesPort24Down);

        Assert.True(result.IsSuccess);
        Assert.Equal(LinkState.Up, result.Value!.Interfaces["1"].OperationalState);
        Assert.Equal(LinkState.Down, result.Value.Interfaces["24"].OperationalState);
        Assert.Null(result.Value.Interfaces["24"].Speed);
    }

    [Fact]
    public void Redactor_RemovesSecretsNetworkAddressesAndMacs()
    {
        var value = "username: operator password=letmein peer=10.10.4.5 ipv6=2001:db8::10 mac=00:11:22:33:44:55 explicit-secret";

        var redacted = DiagnosticRedactor.Redact(value, ["explicit-secret", "letmein"]);

        Assert.DoesNotContain("operator", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("letmein", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("10.10.4.5", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("2001:db8::10", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("00:11:22:33:44:55", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("explicit-secret", redacted, StringComparison.Ordinal);
    }
}
