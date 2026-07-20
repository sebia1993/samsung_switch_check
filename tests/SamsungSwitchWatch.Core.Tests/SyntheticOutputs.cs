namespace SamsungSwitchWatch.Core.Tests;

internal static class SyntheticOutputs
{
    public const string Version = """
        Model Name       : IES4224GP
        Software Version : 2.7.1-test
        Hardware Version : R03
        Main Power Status: normal
        """;

    public const string SystemTenHours = """
        System Up Time : 0 days 10:00:00
        Boot POST Check : PASS
        Memory Test     : PASS
        """;

    public const string SystemOneMinute = """
        System Up Time : 0 days 00:01:00
        Boot POST Check : PASS
        """;

    public const string InterfacesUp = """
        Port      Admin     Link    Speed   Duplex
        1         Enabled   Up      1000M   Full
        24        Enabled   Up      1000M   Full
        """;

    public const string InterfacesPort24Down = """
        Port      Admin     Link    Speed   Duplex
        1         Enabled   Up      1000M   Full
        24        Enabled   Down    --      --
        """;

    public static string Logs(params (int Sequence, string Time, string Message)[] entries) =>
        string.Join(Environment.NewLine, entries.Select(entry => $"""
            [{entry.Sequence}] {entry.Time} 2026-07-20
            "{entry.Message}"
            level: 6, module: 6, function: 1, and event no.: {entry.Sequence}
            """));
}
