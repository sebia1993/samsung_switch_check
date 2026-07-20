using System.Text.RegularExpressions;
using SamsungSwitchWatch.Core.Diagnostics;
using SamsungSwitchWatch.Core.Models;

namespace SamsungSwitchWatch.Core.Parsing;

public static partial class VersionOutputParser
{
    public static ParseResult<VersionSnapshot> Parse(string output)
    {
        var normalized = Normalize(output);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return ParseResult<VersionSnapshot>.Unsupported("parse-version", "Version output was empty.");
        }

        var model = MatchValue(normalized, ModelRegex()) ?? IesModelRegex().Match(normalized).Value.NullIfWhiteSpace();
        var software = MatchValue(normalized, SoftwareRegex());
        var hardware = MatchValue(normalized, HardwareRegex());
        var mainPower = MatchValue(normalized, MainPowerRegex());
        var redundantPower = MatchValue(normalized, RedundantPowerRegex());

        if (model is null && software is null && hardware is null)
        {
            return ParseResult<VersionSnapshot>.Unsupported(
                "parse-version",
                "The version output format is not recognized for this firmware.");
        }

        return ParseResult<VersionSnapshot>.Success(new VersionSnapshot(
            model,
            software,
            hardware,
            mainPower,
            redundantPower));
    }

    private static string Normalize(string output) =>
        Telnet.OutputNormalizer.CleanControlCharacters(output ?? string.Empty);

    private static string? MatchValue(string input, Regex regex)
    {
        var match = regex.Match(input);
        return match.Success ? match.Groups["value"].Value.Trim().NullIfWhiteSpace() : null;
    }

    [GeneratedRegex(@"(?im)^\s*(?:model(?:\s+name)?|product(?:\s+name)?)\s*[:=]\s*(?<value>[^\r\n]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelRegex();

    [GeneratedRegex(@"(?i)\bIES(?:4028XP|4224GP|4226XP)\b", RegexOptions.CultureInvariant)]
    private static partial Regex IesModelRegex();

    [GeneratedRegex(@"(?im)^\s*(?:software|firmware|sw)(?:\s+(?:image\s+)?version)?\s*[:=]\s*(?<value>[^\r\n]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex SoftwareRegex();

    [GeneratedRegex(@"(?im)^\s*(?:hardware|hw)(?:\s+version)?\s*[:=]\s*(?<value>[^\r\n]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex HardwareRegex();

    [GeneratedRegex(@"(?im)^\s*main\s+power(?:\s+status)?\s*[:=]\s*(?<value>[^\r\n]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex MainPowerRegex();

    [GeneratedRegex(@"(?im)^\s*(?:redundant|backup)\s+power(?:\s+status)?\s*[:=]\s*(?<value>[^\r\n]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex RedundantPowerRegex();
}

internal static class ParsingStringExtensions
{
    public static string? NullIfWhiteSpace(this string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
