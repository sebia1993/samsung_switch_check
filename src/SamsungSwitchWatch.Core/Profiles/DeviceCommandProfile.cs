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
                !command.Command.TrimStart().StartsWith("show ", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Profiles may contain only registered show commands.", nameof(commands));
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
}
