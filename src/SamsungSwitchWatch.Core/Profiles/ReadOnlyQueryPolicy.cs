using System.Text.RegularExpressions;

namespace SamsungSwitchWatch.Core.Profiles;

/// <summary>
/// Validates the deliberately small, read-only CLI surface exposed by the Agent.
/// This policy is shared by registered command profiles and ad-hoc Viewer queries
/// so an unsafe command cannot reach credential lookup or the Telnet transport.
/// </summary>
public static partial class ReadOnlyQueryPolicy
{
    public const int MaximumCommandLength = 128;
    public const int MaximumOutputBytes = 64 * 1024;
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

    private static readonly IReadOnlySet<string> AllowedFamilies = new HashSet<string>(
        [
            "port", "ports", "interface", "interfaces", "system", "version",
            "syslog", "sylog", "log", "spanning-tree", "lacp", "power", "memory"
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> DeniedTokens = new HashSet<string>(
        [
            "running-config", "startup-config", "config", "user", "aaa", "radius",
            "tacacs", "snmp", "community", "password", "secret", "key", "crypto"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static ReadOnlyQueryValidation Validate(string? command, int maximumLength = MaximumCommandLength)
    {
        if (maximumLength is < 1 or > MaximumCommandLength)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            return ReadOnlyQueryValidation.Blocked(ReadOnlyQueryRejection.Empty);
        }

        // Check the unmodified value first. Trimming must never hide a second line,
        // control character, or shell-style command separator.
        if (command.Length > maximumLength)
        {
            return ReadOnlyQueryValidation.Blocked(ReadOnlyQueryRejection.TooLong);
        }

        if (command.Any(character => char.IsControl(character) || character is '\u2028' or '\u2029'))
        {
            return ReadOnlyQueryValidation.Blocked(ReadOnlyQueryRejection.ControlCharacter);
        }

        if (command.IndexOfAny([';', '|', '&', '`', '$', '<', '>']) >= 0)
        {
            return ReadOnlyQueryValidation.Blocked(ReadOnlyQueryRejection.Separator);
        }

        var normalized = string.Join(' ', command.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length == 0 || normalized.Length > maximumLength)
        {
            return ReadOnlyQueryValidation.Blocked(
                normalized.Length == 0 ? ReadOnlyQueryRejection.Empty : ReadOnlyQueryRejection.TooLong);
        }

        var parts = normalized.Split(' ');
        if (parts.Length < 2 || !string.Equals(parts[0], "show", StringComparison.OrdinalIgnoreCase))
        {
            return ReadOnlyQueryValidation.Blocked(ReadOnlyQueryRejection.NotShowCommand);
        }

        if (!AllowedFamilies.Contains(parts[1]))
        {
            return ReadOnlyQueryValidation.Blocked(ReadOnlyQueryRejection.FamilyNotAllowed);
        }

        var tokens = CommandTokenRegex().Matches(normalized).Select(match => match.Value);
        if (tokens.Any(DeniedTokens.Contains))
        {
            return ReadOnlyQueryValidation.Blocked(ReadOnlyQueryRejection.SensitiveToken);
        }

        return ReadOnlyQueryValidation.Allowed(normalized);
    }

    public static bool IsAllowed(string? command) => Validate(command).IsAllowed;

    [GeneratedRegex("[A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex CommandTokenRegex();
}

public enum ReadOnlyQueryRejection
{
    None,
    Empty,
    TooLong,
    ControlCharacter,
    Separator,
    NotShowCommand,
    FamilyNotAllowed,
    SensitiveToken
}

public sealed record ReadOnlyQueryValidation(
    bool IsAllowed,
    string? NormalizedCommand,
    ReadOnlyQueryRejection Rejection)
{
    internal static ReadOnlyQueryValidation Allowed(string command) =>
        new(true, command, ReadOnlyQueryRejection.None);

    internal static ReadOnlyQueryValidation Blocked(ReadOnlyQueryRejection rejection) =>
        new(false, null, rejection);
}
