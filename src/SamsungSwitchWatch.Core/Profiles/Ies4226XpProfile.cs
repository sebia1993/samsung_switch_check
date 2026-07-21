namespace SamsungSwitchWatch.Core.Profiles;

/// <summary>
/// Candidate read-only profile for the iES4200 family. Unsupported commands
/// are isolated as collector capability state instead of changing the device.
/// </summary>
public static class Ies4226XpProfile
{
    public static DeviceCommandProfile Create() => SamsungIesProfileFactory.Create("IES4226XP");
}
