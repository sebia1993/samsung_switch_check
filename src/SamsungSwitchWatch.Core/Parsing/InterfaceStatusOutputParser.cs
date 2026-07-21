using System.Text.RegularExpressions;
using SamsungSwitchWatch.Core.Diagnostics;
using SamsungSwitchWatch.Core.Models;

namespace SamsungSwitchWatch.Core.Parsing;

public static partial class InterfaceStatusOutputParser
{
    public static ParseResult<InterfaceStatusSnapshot> Parse(string output, string? requiredInterfaceId = null)
    {
        if (requiredInterfaceId is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(requiredInterfaceId);
        }

        var normalized = Telnet.OutputNormalizer.CleanControlCharacters(output ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return ParseResult<InterfaceStatusSnapshot>.Unsupported("parse-interface-status", "Interface status output was empty.");
        }

        if (NoInterfacesRegex().IsMatch(normalized))
        {
            return ParseResult<InterfaceStatusSnapshot>.Success(
                new InterfaceStatusSnapshot(new Dictionary<string, InterfaceStatus>()));
        }

        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var interfaces = new Dictionary<string, InterfaceStatus>(StringComparer.OrdinalIgnoreCase);
        var headerFound = false;
        ColumnMap? columns = null;

        foreach (var rawLine in lines)
        {
            var tokens = SplitColumns(rawLine);
            if (tokens.Length < 2)
            {
                continue;
            }

            if (!headerFound && TryBuildColumnMap(tokens, out columns))
            {
                headerFound = true;
                continue;
            }

            if (headerFound && TryParseShowPortStatusRow(rawLine, out var showPortStatus))
            {
                interfaces[showPortStatus.PortId] = showPortStatus;
                continue;
            }

            if (headerFound && columns is not null && TryParseMappedRow(tokens, columns, out var mapped))
            {
                interfaces[mapped.PortId] = mapped;
                continue;
            }

            if (TryParseFallback(rawLine, out var fallback))
            {
                interfaces[fallback.PortId] = fallback;
            }
        }

        if (interfaces.Count == 0)
        {
            return headerFound
                ? ParseResult<InterfaceStatusSnapshot>.Failure(
                    ErrorCodes.IncompleteOutput,
                    "parse-interface-status",
                    "The interface table header was present but no complete rows were found.",
                    true)
                : ParseResult<InterfaceStatusSnapshot>.Unsupported(
                    "parse-interface-status",
                    "The interface status table is not recognized for this firmware.");
        }

        if (requiredInterfaceId is not null && !interfaces.ContainsKey(requiredInterfaceId))
        {
            return ParseResult<InterfaceStatusSnapshot>.Failure(
                ErrorCodes.IncompleteOutput,
                "parse-interface-status",
                "The required monitored interface was missing from the collected table.",
                true);
        }

        return ParseResult<InterfaceStatusSnapshot>.Success(new InterfaceStatusSnapshot(interfaces));
    }

    private static bool TryBuildColumnMap(string[] tokens, out ColumnMap? map)
    {
        map = null;
        var normalized = CollapseHeaderTokens(tokens);
        var port = Array.FindIndex(normalized, static value => value is "port" or "slotport" or "interface" or "ifname");
        var admin = Array.FindIndex(normalized, static value => value.StartsWith("admin", StringComparison.Ordinal));
        var oper = Array.FindIndex(normalized, static value =>
            value is "oper" or "operstatus" or "link" or "linkstate" or "status" or "state");
        var speed = Array.FindIndex(normalized, static value => value.StartsWith("speed", StringComparison.Ordinal));
        var duplex = Array.FindIndex(normalized, static value => value.StartsWith("duplex", StringComparison.Ordinal));

        if (port < 0 || oper < 0)
        {
            return false;
        }

        map = new ColumnMap(port, admin, oper, speed, duplex);
        return true;
    }

    private static string[] CollapseHeaderTokens(string[] tokens)
    {
        var normalized = tokens.Select(NormalizeHeader).ToArray();
        var collapsed = new List<string>(normalized.Length);
        for (var index = 0; index < normalized.Length; index++)
        {
            var value = normalized[index];
            if (index + 1 < normalized.Length &&
                (value is "admin" or "administrative" or "oper" or "operational" or "link") &&
                (normalized[index + 1] is "status" or "state"))
            {
                collapsed.Add(value + normalized[++index]);
                continue;
            }

            collapsed.Add(value);
        }

        return collapsed.ToArray();
    }

    private static bool TryParseMappedRow(string[] tokens, ColumnMap map, out InterfaceStatus status)
    {
        status = null!;
        var maximumRequired = new[] { map.Port, map.Operational, map.Admin, map.Speed, map.Duplex }.Max();
        if (tokens.Length <= maximumRequired || !PortIdRegex().IsMatch(tokens[map.Port]))
        {
            return false;
        }

        var operational = ParseOperational(tokens[map.Operational]);
        var admin = map.Admin >= 0 ? ParseAdministrative(tokens[map.Admin]) : AdministrativeState.Unknown;
        if (operational == LinkState.Unknown && admin == AdministrativeState.Unknown)
        {
            return false;
        }

        status = new InterfaceStatus(
            tokens[map.Port],
            admin,
            operational,
            map.Speed >= 0 ? CleanOptional(tokens[map.Speed]) : null,
            map.Duplex >= 0 ? CleanOptional(tokens[map.Duplex]) : null);
        return true;
    }

    private static bool TryParseFallback(string line, out InterfaceStatus status)
    {
        status = null!;
        var match = FallbackRowRegex().Match(line);
        if (!match.Success)
        {
            return false;
        }

        var admin = ParseAdministrative(match.Groups["admin"].Value);
        var operational = ParseOperational(match.Groups["oper"].Value);
        if (admin == AdministrativeState.Unknown && operational == LinkState.Unknown)
        {
            return false;
        }

        status = new InterfaceStatus(
            match.Groups["port"].Value,
            admin,
            operational,
            CleanOptional(match.Groups["speed"].Value),
            CleanOptional(match.Groups["duplex"].Value));
        return true;
    }

    private static bool TryParseShowPortStatusRow(string line, out InterfaceStatus status)
    {
        status = null!;
        var match = ShowPortStatusRowRegex().Match(line);
        if (!match.Success)
        {
            return false;
        }

        var operational = ParseOperational(match.Groups["oper"].Value);
        var admin = ParseAdministrative(match.Groups["admin"].Value);
        if (operational == LinkState.Unknown || admin == AdministrativeState.Unknown)
        {
            return false;
        }

        status = new InterfaceStatus(
            match.Groups["port"].Value,
            admin,
            operational,
            CleanOptional(match.Groups["speed"].Value),
            CleanOptional(match.Groups["duplex"].Value));
        return true;
    }

    private static AdministrativeState ParseAdministrative(string value) => NormalizeState(value) switch
    {
        "enable" or "enabled" or "up" or "on" => AdministrativeState.Enabled,
        "disable" or "disabled" or "down" or "off" => AdministrativeState.Disabled,
        _ => AdministrativeState.Unknown
    };

    private static LinkState ParseOperational(string value) => NormalizeState(value) switch
    {
        "up" or "connected" or "linkup" or "forwarding" => LinkState.Up,
        "down" or "notconnect" or "notconnected" or "linkdown" or "disconnected" => LinkState.Down,
        _ => LinkState.Unknown
    };

    private static string NormalizeState(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string NormalizeHeader(string value) => NormalizeState(value);

    private static string? CleanOptional(string value) => NormalizeState(value) is "" or "na" or "notapplicable" ? null : value;

    private static string[] SplitColumns(string value)
    {
        if (value.Contains('|'))
        {
            return value.Trim().Trim('|').Split('|').Select(static token => token.Trim()).ToArray();
        }

        return Regex.Split(value.Trim(), @"\s+").Where(static token => token.Length > 0).ToArray();
    }

    private sealed record ColumnMap(int Port, int Admin, int Operational, int Speed, int Duplex);

    [GeneratedRegex(@"^(?:[A-Za-z]{1,12}[./:-]?)?\d+(?:[/.:]\d+){0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex PortIdRegex();

    [GeneratedRegex(@"^\s*(?<port>(?:[A-Za-z]{1,12})?\d+(?:[/.:]\d+){0,3})\s+(?<admin>enable(?:d)?|disable(?:d)?|up|down|on|off)\s+(?<oper>up|down|connected|notconnect(?:ed)?|link[ -]?(?:up|down)|disconnected)\s+(?<speed>\S+)\s+(?<duplex>\S+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FallbackRowRegex();

    [GeneratedRegex(@"^\s*(?<port>(?:[A-Za-z]{1,12}[./:-]?)?\d+(?:[/.:]\d+){0,3})\s+(?:\S.*?\s+)?(?<oper>up|down|connected|notconnect(?:ed)?|link[ -]?(?:up|down)|disconnected)\s+(?<admin>enable(?:d)?|disable(?:d)?|up|down|on|off)\s+(?<speed>\S+)\s+(?<duplex>full|half|auto|n/?a|-{1,2})(?:\s+.*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShowPortStatusRowRegex();

    [GeneratedRegex(@"(?is)^\s*no\s+interfaces?(?:\s+(?:found|available|configured))?\s*[.!]?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex NoInterfacesRegex();
}
