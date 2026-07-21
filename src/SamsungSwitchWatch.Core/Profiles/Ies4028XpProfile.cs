namespace SamsungSwitchWatch.Core.Profiles;

/// <summary>
/// Candidate read-only profile for the iES4000 family. Each command remains
/// capability-probed at runtime because firmware variants can differ.
/// </summary>
public static class Ies4028XpProfile
{
    public static DeviceCommandProfile Create() => SamsungIesProfileFactory.Create("IES4028XP");
}
