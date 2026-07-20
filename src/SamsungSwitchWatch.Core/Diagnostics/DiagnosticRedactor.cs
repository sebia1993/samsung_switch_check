using System.Net;
using System.Text.RegularExpressions;

namespace SamsungSwitchWatch.Core.Diagnostics;

public static partial class DiagnosticRedactor
{
    public const string Redacted = "[REDACTED]";

    public static string Redact(string? value, IEnumerable<string?>? secrets = null)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sanitized = value;
        if (secrets is not null)
        {
            foreach (var secret in secrets.Where(static item => !string.IsNullOrWhiteSpace(item)))
            {
                sanitized = sanitized.Replace(secret!, Redacted, StringComparison.Ordinal);
            }
        }

        sanitized = MacAddressRegex().Replace(sanitized, Redacted);
        sanitized = Ipv4CandidateRegex().Replace(sanitized, static match =>
            IPAddress.TryParse(match.Value, out _) ? Redacted : match.Value);
        sanitized = Ipv6CandidateRegex().Replace(sanitized, static match =>
            IPAddress.TryParse(match.Value.Trim('[', ']'), out var address) &&
            address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? Redacted
                : match.Value);
        sanitized = UserAssignmentRegex().Replace(sanitized, "$1" + Redacted);
        sanitized = PasswordAssignmentRegex().Replace(sanitized, "$1" + Redacted);
        sanitized = HostAssignmentRegex().Replace(sanitized, "$1" + Redacted);
        return sanitized;
    }

    [GeneratedRegex(@"(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex MacAddressRegex();

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4CandidateRegex();

    [GeneratedRegex(@"(?<![0-9A-Fa-f:])\[?(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}\]?(?![0-9A-Fa-f:])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6CandidateRegex();

    [GeneratedRegex(@"(?im)\b(user(?:name)?\s*[:=]\s*)[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex UserAssignmentRegex();

    [GeneratedRegex(@"(?im)\b(pass(?:word)?|community|token|secret)(\s*[:=]\s*)[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex PasswordAssignmentRegex();

    [GeneratedRegex(@"(?im)\b((?:host(?:name)?|device)\s*[:=]\s*)[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex HostAssignmentRegex();
}
