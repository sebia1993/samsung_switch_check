using SamsungSwitchWatch.Core.Profiles;

namespace SamsungSwitchWatch.Core.Tests;

public sealed class ReadOnlyQueryPolicyTests
{
    [Theory]
    [InlineData("show port status")]
    [InlineData("show interfaces status")]
    [InlineData("show sylog tail num 100")]
    [InlineData("show syslog tail num 100")]
    [InlineData("show spanning-tree")]
    [InlineData("show lacp neighbors")]
    [InlineData("SHOW VERSION")]
    public void Validate_AllowsApprovedShowFamilies(string command)
    {
        var result = ReadOnlyQueryPolicy.Validate(command);

        Assert.True(result.IsAllowed);
        Assert.Equal(command, result.NormalizedCommand);
        Assert.Equal(ReadOnlyQueryRejection.None, result.Rejection);
    }

    [Fact]
    public void Validate_NormalizesRepeatedSpaces()
    {
        var result = ReadOnlyQueryPolicy.Validate("  show   port   status  ");

        Assert.True(result.IsAllowed);
        Assert.Equal("show port status", result.NormalizedCommand);
    }

    [Theory]
    [InlineData("configure terminal", ReadOnlyQueryRejection.NotShowCommand)]
    [InlineData("show running-config", ReadOnlyQueryRejection.FamilyNotAllowed)]
    [InlineData("show system password", ReadOnlyQueryRejection.SensitiveToken)]
    [InlineData("show port config", ReadOnlyQueryRejection.SensitiveToken)]
    [InlineData("show vlan", ReadOnlyQueryRejection.FamilyNotAllowed)]
    [InlineData("show port status; reload", ReadOnlyQueryRejection.Separator)]
    [InlineData("show port status | include Up", ReadOnlyQueryRejection.Separator)]
    [InlineData("show port status\nreload", ReadOnlyQueryRejection.ControlCharacter)]
    [InlineData("show port status\u2028reload", ReadOnlyQueryRejection.ControlCharacter)]
    public void Validate_BlocksUnsafeCommands(string command, ReadOnlyQueryRejection expected)
    {
        var result = ReadOnlyQueryPolicy.Validate(command);

        Assert.False(result.IsAllowed);
        Assert.Null(result.NormalizedCommand);
        Assert.Equal(expected, result.Rejection);
    }

    [Fact]
    public void Validate_RejectsCommandsLongerThanConfiguredLimit()
    {
        var result = ReadOnlyQueryPolicy.Validate("show port " + new string('x', 119));

        Assert.False(result.IsAllowed);
        Assert.Equal(ReadOnlyQueryRejection.TooLong, result.Rejection);
    }

    [Fact]
    public void DeviceProfileRequiresAlreadyNormalizedRegisteredCommands()
    {
        var baseProfile = Ies4224GpProfile.Create();

        Assert.Throws<ArgumentException>(() => new DeviceCommandProfile(
            baseProfile.Model,
            baseProfile.Telnet,
            [new ReadOnlyCommandDefinition(
                "query",
                "Query",
                " show port status ",
                TimeSpan.FromSeconds(1),
                60)]));
    }
}
