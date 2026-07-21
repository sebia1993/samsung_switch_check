using SamsungSwitchWatch.Core.Profiles;

namespace SamsungSwitchWatch.Core.Tests;

public sealed class DeviceProfileRegistryTests
{
    [Fact]
    public void SamsungRegistryResolvesAllSupportedModelsCaseInsensitively()
    {
        var registry = CreateRegistry();

        Assert.Equal(["IES4028XP", "IES4224GP", "IES4226XP"], registry.Models);
        Assert.Equal("IES4224GP", registry.GetRequired("ies4224gp").Model);
        Assert.Equal("IES4028XP", registry.GetRequired("IES4028XP").Model);
        Assert.Equal("IES4226XP", registry.GetRequired("ies4226xp").Model);
    }

    [Fact]
    public void EveryRegisteredProfileContainsOnlyTheApprovedShowCommands()
    {
        var registry = CreateRegistry();
        var expected = new[]
        {
            CommandIds.Version,
            CommandIds.System,
            CommandIds.LogRam,
            CommandIds.InterfaceStatus
        };

        foreach (var model in registry.Models)
        {
            var profile = registry.GetRequired(model);
            Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase),
                profile.Commands.Select(command => command.Id).Order(StringComparer.OrdinalIgnoreCase));
            Assert.All(profile.Commands, command =>
            {
                Assert.All(command.CandidateCommands, candidate =>
                {
                    Assert.StartsWith("show ", candidate, StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(';', candidate);
                    Assert.DoesNotContain('|', candidate);
                });
            });
        }
    }

    [Fact]
    public void OperationalCollectorsPreferRequestedCommandsAndKeepCompatibleIds()
    {
        foreach (var model in CreateRegistry().Models)
        {
            var profile = CreateRegistry().GetRequired(model);
            Assert.Equal(
                ["show port status", "show interfaces status"],
                profile.GetRequiredCommand(CommandIds.InterfaceStatus).CandidateCommands);
            Assert.Equal(
                ["show syslog tail num 100", "show log ram"],
                profile.GetRequiredCommand(CommandIds.LogRam).CandidateCommands);
        }
    }

    [Fact]
    public void CandidateCommandsAreDeduplicatedAndCanSelectFallbackWithoutChangingContract()
    {
        var definition = new ReadOnlyCommandDefinition(
            CommandIds.InterfaceStatus,
            "포트 상태",
            "show port status",
            TimeSpan.FromSeconds(60),
            60,
            ["SHOW PORT STATUS", "show interfaces status"]);

        Assert.Equal(["show port status", "show interfaces status"], definition.CandidateCommands);

        var selected = definition.WithCommand("SHOW INTERFACES STATUS");
        Assert.Equal(CommandIds.InterfaceStatus, selected.Id);
        Assert.Equal("포트 상태", selected.DisplayName);
        Assert.Equal(TimeSpan.FromSeconds(60), selected.Timeout);
        Assert.Equal(60, selected.DefaultIntervalSeconds);
        Assert.Equal("show interfaces status", selected.Command);
    }

    [Fact]
    public void ProfileRejectsUnsafeFallbackCommand()
    {
        var baseProfile = Ies4224GpProfile.Create();

        Assert.Throws<ArgumentException>(() => new DeviceCommandProfile(
            baseProfile.Model,
            baseProfile.Telnet,
            [new ReadOnlyCommandDefinition(
                CommandIds.InterfaceStatus,
                "포트 상태",
                "show port status",
                TimeSpan.FromSeconds(60),
                60,
                ["show interfaces status; reload"])]));
    }

    private static DeviceProfileRegistry CreateRegistry() => new(
    [
        Ies4224GpProfile.Create(),
        Ies4028XpProfile.Create(),
        Ies4226XpProfile.Create()
    ]);
}
