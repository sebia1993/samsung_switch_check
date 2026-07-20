using System.Collections.ObjectModel;

namespace SamsungSwitchWatch.Core.Profiles;

public sealed record ReadOnlyCommandDefinition(
    string Id,
    string DisplayName,
    string Command,
    TimeSpan Timeout,
    int DefaultIntervalSeconds);

public sealed record TelnetPromptProfile(
    string LoginPromptPattern,
    string PasswordPromptPattern,
    string DevicePromptPattern,
    IReadOnlyList<string> AuthenticationFailurePatterns,
    IReadOnlyList<string> PagingMarkers,
    byte PagingContinueByte = 0x20,
    string LogoutCommand = "exit");

public sealed class DeviceCommandProfile
{
    private readonly IReadOnlyDictionary<string, ReadOnlyCommandDefinition> _commands;

    public DeviceCommandProfile(
        string model,
        TelnetPromptProfile telnet,
        IEnumerable<ReadOnlyCommandDefinition> commands)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(telnet);
        ArgumentNullException.ThrowIfNull(commands);

        Model = model;
        Telnet = telnet;
        var materialized = commands.ToArray();
        if (materialized.Length == 0 || materialized.Any(static command =>
                string.IsNullOrWhiteSpace(command.Id) ||
                string.IsNullOrWhiteSpace(command.Command) ||
                !IsSafeReadOnlyCommand(command.Command) ||
                command.Timeout <= TimeSpan.Zero ||
                command.DefaultIntervalSeconds <= 0))
        {
            throw new ArgumentException("Profiles may contain only registered show commands.", nameof(commands));
        }

        if (materialized.Select(static command => command.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != materialized.Length)
        {
            throw new ArgumentException("Command IDs must be unique.", nameof(commands));
        }

        _commands = new ReadOnlyDictionary<string, ReadOnlyCommandDefinition>(
            materialized.ToDictionary(static command => command.Id, StringComparer.OrdinalIgnoreCase));
    }

    public string Model { get; }

    public TelnetPromptProfile Telnet { get; }

    public IReadOnlyCollection<ReadOnlyCommandDefinition> Commands => _commands.Values.ToArray();

    public bool TryGetCommand(string commandId, out ReadOnlyCommandDefinition command) =>
        _commands.TryGetValue(commandId, out command!);

    public ReadOnlyCommandDefinition GetRequiredCommand(string commandId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandId);
        if (!_commands.TryGetValue(commandId, out var command))
        {
            throw new ArgumentOutOfRangeException(nameof(commandId), commandId, "The command ID is not registered for this device profile.");
        }

        return command;
    }

    private static bool IsSafeReadOnlyCommand(string command)
    {
        if (!string.Equals(command, command.Trim(), StringComparison.Ordinal) ||
            !command.StartsWith("show ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return command.All(static character =>
            !char.IsControl(character) && character is not ';' and not '|' and not '&' and not '`' and not '$' and not '<' and not '>');
    }
}
