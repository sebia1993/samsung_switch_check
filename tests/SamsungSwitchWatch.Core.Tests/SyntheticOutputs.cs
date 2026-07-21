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

    public const string PortStatusWithAlias = """
        Port       Alias          Oper Status  Admin Status  Speed  Duplex  Type
        ---------- -------------- ------------ ------------- ------ ------- ------------
        ge.1.1     user-floor     Up           Up            1.0G   full    RJ45
        ge.1.24                   Down         Up            N/A    N/A     SFP
        """;

    public const string PipeDelimitedPortStatus = """
        Port | Alias | Oper | Admin | Speed | Duplex | Type
        ge.1.1 | uplink | up | up | 1000M | full | RJ45
        ge.1.2 | | down | disabled | -- | -- | RJ45
        """;

    public const string SyslogTail = """
        41 2026-07-20 14:01:00 Port 1 link down
        42 2026-07-20 14:01:02 Port 1 link up
        2026-07-20 14:02:00 [warning] STP root changed
        """;

    public const string SyslogTailTimeFirst = """
        [51] 14:03:00 2026-07-20 Uplink state changed
        14:04:00 2026-07-20 Power status normal
        """;

    public static string Logs(params (int Sequence, string Time, string Message)[] entries) =>
        string.Join(Environment.NewLine, entries.Select(entry => $"""
            [{entry.Sequence}] {entry.Time} 2026-07-20
            "{entry.Message}"
            level: 6, module: 6, function: 1, and event no.: {entry.Sequence}
            """));
}
