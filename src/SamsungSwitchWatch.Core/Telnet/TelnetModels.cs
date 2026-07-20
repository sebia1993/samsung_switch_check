namespace SamsungSwitchWatch.Core.Telnet;

public sealed record TelnetEndpoint(string Host, int Port = 23);

public sealed class TelnetCredentials
{
    public TelnetCredentials(string username, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentNullException.ThrowIfNull(password);
        if (username.Length > 128 || username.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("Telnet username must be at most 128 characters and contain no line or NUL characters.", nameof(username));
        }
        if (password.Length is 0 or > 512 || password.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("Telnet password must be 1 to 512 characters and contain no line or NUL characters.", nameof(password));
        }
        Username = username;
        Password = password;
    }

    public string Username { get; }

    public string Password { get; }

    public override string ToString() => "TelnetCredentials { Username = [REDACTED], Password = [REDACTED] }";
}

public sealed record TelnetTimeouts(
    TimeSpan Connect,
    TimeSpan LoginPrompt,
    TimeSpan Authentication,
    TimeSpan Logout)
{
    public TimeSpan Write { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan Session { get; init; } = TimeSpan.FromMinutes(3);

    public static TelnetTimeouts Default { get; } = new(
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(2))
    {
        Write = TimeSpan.FromSeconds(5),
        Session = TimeSpan.FromMinutes(3)
    };
}

public sealed record TelnetClientOptions(
    TelnetTimeouts Timeouts,
    int MaximumOutputBytes = 2 * 1024 * 1024,
    int ReadBufferBytes = 4096,
    int MaximumNegotiationBytesWithoutText = 16 * 1024,
    int MaximumWireBytes = 2 * 1024 * 1024,
    int DetectionWindowCharacters = 16 * 1024)
{
    public static TelnetClientOptions Default { get; } = new(TelnetTimeouts.Default);
}

public sealed record CommandOutput(
    string CommandId,
    string Command,
    string RawOutput,
    string NormalizedOutput,
    DateTimeOffset CollectedAt);

public sealed record TelnetSessionResult(
    string Model,
    IReadOnlyList<CommandOutput> Outputs,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);
