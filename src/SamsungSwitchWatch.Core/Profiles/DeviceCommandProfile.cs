using System.Collections.ObjectModel;

namespace SamsungSwitchWatch.Core.Profiles;

public sealed record ReadOnlyCommandDefinition(
    string Id,
    string DisplayName,
    string Command,
    TimeSpan Timeout,
    int DefaultIntervalSeconds,
    IReadOnlyList<string>? FallbackCommands = null)
{
    /// <summary>
    /// Gets the ordered, case-insensitively de-duplicated CLI candidates for
    /// this stable command ID. The first item is the preferred command.
    /// </summary>
    public IReadOnlyList<string> CandidateCommands =>
        [.. new[] { Command }
            .Concat(FallbackCommands ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Returns an equivalent definition with a capability-probed candidate
    /// selected as the command that Telnet will execute.
    /// </summary>
    public ReadOnlyCommandDefinition WithCommand(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        var selected = CandidateCommands.FirstOrDefault(candidate =>
            string.Equals(candidate, command, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            throw new ArgumentOutOfRangeException(nameof(command), command,
                "The command is not a registered candidate for this definition.");
        }

        return new ReadOnlyCommandDefinition(
            Id,
            DisplayName,
            selected,
            Timeout,
            DefaultIntervalSeconds,
            CandidateCommands.Where(candidate =>
                !string.Equals(candidate, selected, StringComparison.OrdinalIgnoreCase)).ToArray());
    }
}

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
                string.IsNullOrWhiteSpace(command.DisplayName) ||
                command.CandidateCommands.Count == 0 ||
                command.CandidateCommands.Any(candidate =>
                {
                    var validation = ReadOnlyQueryPolicy.Validate(candidate);
                    return string.IsNullOrWhiteSpace(candidate) ||
                           !validation.IsAllowed ||
                           !string.Equals(candidate, validation.NormalizedCommand, StringComparison.Ordinal);
                }) ||
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

    public DeviceCommandProfile WithCommand(string commandId, string command)
    {
        var selected = GetRequiredCommand(commandId).WithCommand(command);
        return new DeviceCommandProfile(
            Model,
            Telnet,
            _commands.Values.Select(item =>
                string.Equals(item.Id, commandId, StringComparison.OrdinalIgnoreCase) ? selected : item));
    }

}
