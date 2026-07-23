namespace SamsungSwitchWatch.Core.Profiles;

internal static class SamsungIesProfileFactory
{
    public static DeviceCommandProfile Create(string model) => new(
        model,
        new TelnetPromptProfile(
            LoginPromptPattern: @"(?im)(?:login|user(?:name)?)[ \t]*:[ \t]*\r?$",
            PasswordPromptPattern: @"(?im)password[ \t]*:[ \t]*\r?$",
            DevicePromptPattern: @"(?m)^[^\r\n]{1,80}[>#][ \t]*\r?$",
            AuthenticationFailurePatterns:
            [
                @"(?i)authentication\s+failed",
                @"(?i)login\s+(?:incorrect|failed)",
                @"(?i)(?:invalid|incorrect)\s+(?:user(?:name)?|password)",
                @"(?i)access\s+denied"
            ],
            PagingMarkers:
            [
                "--More--",
                "---- More ----",
                "Press any key to continue",
                "Press SPACE to continue"
            ]),
        [
            new ReadOnlyCommandDefinition(CommandIds.Version, "버전", "show version", TimeSpan.FromSeconds(30), 3600),
            new ReadOnlyCommandDefinition(CommandIds.System, "시스템 상태", "show system", TimeSpan.FromSeconds(30), 300),
            new ReadOnlyCommandDefinition(
                CommandIds.LogRam,
                "시스템 로그",
                "show sylog tail num 100",
                TimeSpan.FromSeconds(60),
                60,
                ["show syslog tail num 100", "show log ram"]),
            new ReadOnlyCommandDefinition(
                CommandIds.InterfaceStatus,
                "포트 상태",
                "show port status",
                TimeSpan.FromSeconds(60),
                60,
                ["show interfaces status"])
        ]);
}
