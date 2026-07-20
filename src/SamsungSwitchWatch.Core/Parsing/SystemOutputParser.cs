using System.Globalization;
using System.Text.RegularExpressions;
using SamsungSwitchWatch.Core.Diagnostics;
using SamsungSwitchWatch.Core.Models;

namespace SamsungSwitchWatch.Core.Parsing;

public static partial class SystemOutputParser
{
    public static ParseResult<SystemSnapshot> Parse(string output)
    {
        var normalized = NormalizeLineEndings(
            Telnet.OutputNormalizer.CleanControlCharacters(output ?? string.Empty));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return ParseResult<SystemSnapshot>.Unsupported("parse-system", "System output was empty.");
        }

        TimeSpan? uptime = null;
        var uptimeMatch = UptimeLineRegex().Match(normalized);
        if (uptimeMatch.Success)
        {
            uptime = ParseUptime(uptimeMatch.Groups["value"].Value);
        }

        var checks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in PostCheckRegex().Matches(normalized))
        {
            var name = match.Groups["name"].Value.Trim();
            var value = match.Groups["value"].Value.Trim().ToUpperInvariant();
            if (!string.IsNullOrWhiteSpace(name))
            {
                checks[name] = value;
            }
        }

        if (uptime is null && checks.Count == 0)
        {
            return ParseResult<SystemSnapshot>.Unsupported(
                "parse-system",
                "The system output format is not recognized for this firmware.");
        }

        return ParseResult<SystemSnapshot>.Success(new SystemSnapshot(uptime, checks));
    }

    internal static TimeSpan? ParseUptime(string value)
    {
        var days = ReadUnit(value, DaysRegex());
        var hours = ReadUnit(value, HoursRegex());
        var minutes = ReadUnit(value, MinutesRegex());
        var seconds = ReadUnit(value, SecondsRegex());
        var clock = ClockRegex().Match(value);
        if (clock.Success)
        {
            hours ??= ParseInt(clock.Groups["hours"].Value);
            minutes ??= ParseInt(clock.Groups["minutes"].Value);
            seconds ??= ParseInt(clock.Groups["seconds"].Value);
        }

        if (days is null && hours is null && minutes is null && seconds is null)
        {
            return null;
        }

        try
        {
            return TimeSpan.FromDays(days ?? 0) +
                   TimeSpan.FromHours(hours ?? 0) +
                   TimeSpan.FromMinutes(minutes ?? 0) +
                   TimeSpan.FromSeconds(seconds ?? 0);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static int? ReadUnit(string value, Regex regex)
    {
        var match = regex.Match(value);
        return match.Success ? ParseInt(match.Groups["value"].Value) : null;
    }

    private static int? ParseInt(string value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static string NormalizeLineEndings(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    [GeneratedRegex(@"(?im)^\s*(?:system\s+)?(?:up\s*time|uptime)\s*[:=]\s*(?<value>[^\r\n]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex UptimeLineRegex();

    [GeneratedRegex(@"(?im)^\s*(?<name>[^:\r\n]{1,80}?(?:post|test|check)[^:\r\n]{0,40})\s*[:=]\s*(?<value>PASS|FAIL|OK|ERROR)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PostCheckRegex();

    [GeneratedRegex(@"(?i)(?<value>\d+)\s*d(?:ay)?s?\b", RegexOptions.CultureInvariant)]
    private static partial Regex DaysRegex();

    [GeneratedRegex(@"(?i)(?<value>\d+)\s*h(?:our)?s?\b", RegexOptions.CultureInvariant)]
    private static partial Regex HoursRegex();

    [GeneratedRegex(@"(?i)(?<value>\d+)\s*m(?:in(?:ute)?)?s?\b", RegexOptions.CultureInvariant)]
    private static partial Regex MinutesRegex();

    [GeneratedRegex(@"(?i)(?<value>\d+)\s*s(?:ec(?:ond)?)?s?\b", RegexOptions.CultureInvariant)]
    private static partial Regex SecondsRegex();

    [GeneratedRegex(@"(?<!\d)(?<hours>\d{1,3}):(?<minutes>\d{2}):(?<seconds>\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex ClockRegex();
}
