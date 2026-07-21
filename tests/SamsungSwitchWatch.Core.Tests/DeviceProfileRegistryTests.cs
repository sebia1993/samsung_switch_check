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
                Assert.StartsWith("show ", command.Command, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(';', command.Command);
                Assert.DoesNotContain('|', command.Command);
            });
        }
    }

    private static DeviceProfileRegistry CreateRegistry() => new(
    [
        Ies4224GpProfile.Create(),
        Ies4028XpProfile.Create(),
        Ies4226XpProfile.Create()
    ]);
}
