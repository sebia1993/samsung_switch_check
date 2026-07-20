using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using SamsungSwitchWatch.Core.Diagnostics;
using SamsungSwitchWatch.Core.Profiles;
using SamsungSwitchWatch.Core.Transport;

namespace SamsungSwitchWatch.Core.Telnet;

public sealed class TelnetClient : ITelnetClient
{
    private static readonly Encoding WireEncoding = Encoding.Latin1;
    private readonly IByteTransportFactory _transportFactory;
    private readonly TelnetClientOptions _options;
    private readonly TimeProvider _timeProvider;

    public TelnetClient(
        IByteTransportFactory? transportFactory = null,
        TelnetClientOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _transportFactory = transportFactory ?? new TcpByteTransportFactory();
        _options = options ?? TelnetClientOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_options.MaximumOutputBytes is < 1024 or > 2 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The output limit must be between 1 KiB and 2 MiB.");
        }
    }

    public async Task<TelnetSessionResult> ExecuteRegisteredAsync(
        TelnetEndpoint endpoint,
        TelnetCredentials credentials,
        DeviceCommandProfile profile,
        IReadOnlyCollection<string> commandIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(commandIds);
        if (commandIds.Count == 0)
        {
            throw new ArgumentException("At least one registered command ID is required.", nameof(commandIds));
        }

        var commands = commandIds.Select(profile.GetRequiredCommand).DistinctBy(static item => item.Id).ToArray();
        var startedAt = _timeProvider.GetUtcNow();
        await using var transport = _transportFactory.Create();
        var negotiator = new TelnetNegotiator();
        var authenticated = false;

        try
        {
            await ConnectAsync(transport, endpoint, cancellationToken).ConfigureAwait(false);
            await AuthenticateAsync(transport, negotiator, credentials, profile.Telnet, cancellationToken).ConfigureAwait(false);
            authenticated = true;

            var outputs = new List<CommandOutput>(commands.Length);
            foreach (var command in commands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WriteLineAsync(transport, command.Command, cancellationToken).ConfigureAwait(false);
                var raw = await ReadUntilAsync(
                        transport,
                        negotiator,
                        [profile.Telnet.DevicePromptPattern],
                        [],
                        profile.Telnet.PagingMarkers,
                        profile.Telnet.PagingContinueByte,
                        command.Timeout,
                        ErrorCodes.CommandTimeout,
                        "command",
                        cancellationToken)
                    .ConfigureAwait(false);

                outputs.Add(new CommandOutput(
                    command.Id,
                    command.Command,
                    raw.Text,
                    OutputNormalizer.NormalizeCommandOutput(
                        raw.Text,
                        command.Command,
                        profile.Telnet.DevicePromptPattern,
                        profile.Telnet.PagingMarkers),
                    _timeProvider.GetUtcNow()));
            }

            return new TelnetSessionResult(profile.Model, outputs, startedAt, _timeProvider.GetUtcNow());
        }
        catch (SwitchWatchException)
        {
            throw;
        }
        catch (TelnetProtocolException exception)
        {
            throw Failure(
                ErrorCodes.TelnetNegotiationFailed,
                "telnet-negotiation",
                "The Telnet option negotiation did not complete safely.",
                true,
                exception);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or SocketException or InvalidOperationException)
        {
            throw Failure(
                ErrorCodes.PromptParseFailed,
                authenticated ? "command" : "telnet-session",
                "The switch closed the Telnet session before a valid prompt was received.",
                true,
                exception);
        }
        finally
        {
            if (authenticated && transport.IsConnected)
            {
                await TryLogoutAsync(transport, profile.Telnet.LogoutCommand).ConfigureAwait(false);
            }

            await transport.CloseAsync().ConfigureAwait(false);
        }
    }

    private async Task ConnectAsync(IByteTransport transport, TelnetEndpoint endpoint, CancellationToken cancellationToken)
    {
        using var stageCancellation = CreateStageCancellation(cancellationToken, _options.Timeouts.Connect);
        try
        {
            await transport.ConnectAsync(endpoint.Host, endpoint.Port, stageCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure(ErrorCodes.TcpTimeout, "tcp-connect", "TCP connection timed out.", true, exception);
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            throw Failure(
                ErrorCodes.TelnetNegotiationFailed,
                "tcp-connect",
                "The TCP connection could not be established.",
                true,
                exception);
        }
    }

    private async Task AuthenticateAsync(
        IByteTransport transport,
        TelnetNegotiator negotiator,
        TelnetCredentials credentials,
        TelnetPromptProfile promptProfile,
        CancellationToken cancellationToken)
    {
        var initial = await ReadUntilAsync(
                transport,
                negotiator,
                [promptProfile.LoginPromptPattern, promptProfile.PasswordPromptPattern, promptProfile.DevicePromptPattern],
                promptProfile.AuthenticationFailurePatterns,
                promptProfile.PagingMarkers,
                promptProfile.PagingContinueByte,
                _options.Timeouts.LoginPrompt,
                ErrorCodes.LoginPromptNotFound,
                "login-prompt",
                cancellationToken)
            .ConfigureAwait(false);

        if (initial.IsFailure)
        {
            throw AuthFailure();
        }

        if (initial.PatternIndex == 2)
        {
            return;
        }

        if (initial.PatternIndex == 0)
        {
            await WriteLineAsync(transport, credentials.Username, cancellationToken).ConfigureAwait(false);
            var passwordPrompt = await ReadUntilAsync(
                    transport,
                    negotiator,
                    [promptProfile.PasswordPromptPattern, promptProfile.LoginPromptPattern, promptProfile.DevicePromptPattern],
                    promptProfile.AuthenticationFailurePatterns,
                    promptProfile.PagingMarkers,
                    promptProfile.PagingContinueByte,
                    _options.Timeouts.Authentication,
                    ErrorCodes.AuthFailed,
                    "authentication",
                    cancellationToken)
                .ConfigureAwait(false);

            if (passwordPrompt.IsFailure || passwordPrompt.PatternIndex == 1)
            {
                throw AuthFailure();
            }

            if (passwordPrompt.PatternIndex == 2)
            {
                return;
            }
        }

        await WriteLineAsync(transport, credentials.Password, cancellationToken).ConfigureAwait(false);
        var authenticated = await ReadUntilAsync(
                transport,
                negotiator,
                [promptProfile.DevicePromptPattern, promptProfile.LoginPromptPattern, promptProfile.PasswordPromptPattern],
                promptProfile.AuthenticationFailurePatterns,
                promptProfile.PagingMarkers,
                promptProfile.PagingContinueByte,
                _options.Timeouts.Authentication,
                ErrorCodes.AuthFailed,
                "authentication",
                cancellationToken)
            .ConfigureAwait(false);

        if (authenticated.IsFailure || authenticated.PatternIndex != 0)
        {
            throw AuthFailure();
        }
    }

    private async Task<ReadMatch> ReadUntilAsync(
        IByteTransport transport,
        TelnetNegotiator negotiator,
        IReadOnlyList<string> terminalPatterns,
        IReadOnlyList<string> failurePatterns,
        IReadOnlyList<string> pagingMarkers,
        byte pagingContinueByte,
        TimeSpan timeout,
        string timeoutCode,
        string stage,
        CancellationToken cancellationToken)
    {
        var terminals = terminalPatterns.Select(CreateRegex).ToArray();
        var failures = failurePatterns.Select(CreateRegex).ToArray();
        var rawOutput = new StringBuilder();
        var visibleOutput = new StringBuilder();
        var receivedBytes = 0;
        var buffer = new byte[_options.ReadBufferBytes];
        using var stageCancellation = CreateStageCancellation(cancellationToken, timeout);

        try
        {
            while (true)
            {
                var read = await transport.ReadAsync(buffer, stageCancellation.Token).ConfigureAwait(false);
                if (read == 0)
                {
                    throw Failure(
                        ErrorCodes.PromptParseFailed,
                        stage,
                        "The Telnet stream ended before the expected prompt.",
                        true);
                }

                var frame = negotiator.Process(buffer.AsSpan(0, read), _options.MaximumNegotiationBytesWithoutText);
                if (frame.Responses.Length > 0)
                {
                    await transport.WriteAsync(frame.Responses, stageCancellation.Token).ConfigureAwait(false);
                }

                receivedBytes += frame.Text.Length;
                if (receivedBytes > _options.MaximumOutputBytes)
                {
                    throw Failure(
                        ErrorCodes.OutputLimitExceeded,
                        stage,
                        "The switch response exceeded the configured 2 MiB safety limit.");
                }

                if (frame.Text.Length > 0)
                {
                    var text = WireEncoding.GetString(frame.Text);
                    rawOutput.Append(text);
                    visibleOutput.Append(text);
                }

                foreach (var marker in pagingMarkers)
                {
                    var markerIndex = IndexOf(visibleOutput, marker);
                    while (markerIndex >= 0)
                    {
                        visibleOutput.Remove(markerIndex, marker.Length);
                        await transport.WriteAsync(new byte[] { pagingContinueByte }, stageCancellation.Token).ConfigureAwait(false);
                        markerIndex = IndexOf(visibleOutput, marker);
                    }
                }

                var visibleText = OutputNormalizer.CleanControlCharacters(visibleOutput.ToString());
                for (var index = 0; index < failures.Length; index++)
                {
                    if (failures[index].IsMatch(visibleText))
                    {
                        return new ReadMatch(rawOutput.ToString(), index, true);
                    }
                }

                var terminalIndex = FindTerminalAtEnd(terminals, visibleText);
                if (terminalIndex >= 0)
                {
                    return new ReadMatch(rawOutput.ToString(), terminalIndex, false);
                }
            }
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure(timeoutCode, stage, $"The {stage} stage timed out.", true, exception);
        }
    }

    private async Task TryLogoutAsync(IByteTransport transport, string logoutCommand)
    {
        using var logoutCancellation = new CancellationTokenSource(_options.Timeouts.Logout);
        try
        {
            await WriteLineAsync(transport, logoutCommand, logoutCancellation.Token).ConfigureAwait(false);
        }
        catch
        {
            // The connection is closed in the caller's finally block. Logout
            // failures must never hide the original collection result.
        }
    }

    private static async Task WriteLineAsync(IByteTransport transport, string value, CancellationToken cancellationToken)
    {
        var bytes = WireEncoding.GetBytes(value + "\r\n");
        await transport.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private static Regex CreateRegex(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static CancellationTokenSource CreateStageCancellation(CancellationToken outer, TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(outer);
        source.CancelAfter(timeout);
        return source;
    }

    private static int IndexOf(StringBuilder builder, string value)
    {
        if (builder.Length < value.Length)
        {
            return -1;
        }

        return builder.ToString().IndexOf(value, StringComparison.OrdinalIgnoreCase);
    }

    private static int FindTerminalAtEnd(IReadOnlyList<Regex> patterns, string text)
    {
        for (var index = 0; index < patterns.Count; index++)
        {
            foreach (Match match in patterns[index].Matches(text))
            {
                if (text.AsSpan(match.Index + match.Length).Trim().IsEmpty)
                {
                    return index;
                }
            }
        }

        return -1;
    }

    private static SwitchWatchException AuthFailure() => Failure(
        ErrorCodes.AuthFailed,
        "authentication",
        "The switch rejected the supplied credentials.");

    private static SwitchWatchException Failure(
        string code,
        string stage,
        string message,
        bool retryable = false,
        Exception? exception = null) =>
        new(new DiagnosticError(code, stage, message, retryable), exception);

    private sealed record ReadMatch(string Text, int PatternIndex, bool IsFailure);
}
