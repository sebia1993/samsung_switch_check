namespace SamsungSwitchWatch.Core.Profiles;

/// <summary>
/// Validates the Viewer-driven, read-only CLI surface exposed by the Agent.
/// Every single-line show command is accepted, including running-config, while
/// control characters and command separators are rejected before transport use.
/// </summary>
public static class ReadOnlyQueryPolicy
{
    public const int MaximumCommandLength = 128;
    public const int MaximumOutputBytes = 64 * 1024;
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

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

        return ReadOnlyQueryValidation.Allowed(normalized);
    }

    public static bool IsAllowed(string? command) => Validate(command).IsAllowed;
}

public enum ReadOnlyQueryRejection
{
    None,
    Empty,
    TooLong,
    ControlCharacter,
    Separator,
    NotShowCommand
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
