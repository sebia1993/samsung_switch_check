using System.Text;
using System.Text.RegularExpressions;

namespace SamsungSwitchWatch.Core.Telnet;

public static partial class OutputNormalizer
{
    public static string NormalizeCommandOutput(
        string rawOutput,
        string command,
        string devicePromptPattern,
        IReadOnlyList<string> pagingMarkers)
    {
        ArgumentNullException.ThrowIfNull(rawOutput);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(devicePromptPattern);

        var markerSet = pagingMarkers
            .Where(static marker => !string.IsNullOrWhiteSpace(marker))
            .Select(static marker => marker.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lines = CleanControlCharacters(rawOutput)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(static line => line.TrimEnd())
            .Where(line => !markerSet.Contains(line.Trim()))
            .ToList();

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[0]))
        {
            lines.RemoveAt(0);
        }

        if (lines.Count > 0 && string.Equals(lines[0].Trim(), command.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            lines.RemoveAt(0);
        }

        var promptRegex = new Regex(devicePromptPattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        if (lines.Count > 0 && promptRegex.IsMatch(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string CleanControlCharacters(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var noOsc = OscRegex().Replace(input, string.Empty);
        var noAnsi = AnsiCsiRegex().Replace(noOsc, string.Empty);
        var builder = new StringBuilder(noAnsi.Length);
        foreach (var character in noAnsi)
        {
            if (character == '\b')
            {
                if (builder.Length > 0 && builder[^1] is not '\r' and not '\n')
                {
                    builder.Length--;
                }

                continue;
            }

            if (character is '\r' or '\n' or '\t' || !char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    [GeneratedRegex(@"\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)", RegexOptions.CultureInvariant)]
    private static partial Regex OscRegex();

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|[@-_])", RegexOptions.CultureInvariant)]
    private static partial Regex AnsiCsiRegex();
}
