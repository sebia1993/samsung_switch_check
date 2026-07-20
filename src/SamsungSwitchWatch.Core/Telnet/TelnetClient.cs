using System.Net;
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

        if (_options.MaximumWireBytes is < 1024 or > 4 * 1024 * 1024 ||
            _options.ReadBufferBytes is < 256 or > 64 * 1024 ||
            _options.MaximumNegotiationBytesWithoutText is < 3 or > 64 * 1024 ||
            _options.DetectionWindowCharacters is < 512 or > 64 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Telnet stream limits are outside the supported safety range.");
        }

        var timeouts = _options.Timeouts;
        if (timeouts.Connect <= TimeSpan.Zero || timeouts.LoginPrompt <= TimeSpan.Zero ||
            timeouts.Authentication <= TimeSpan.Zero || timeouts.Logout <= TimeSpan.Zero ||
            timeouts.Write <= TimeSpan.Zero || timeouts.Session <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Telnet timeouts must be positive.");
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

        if (IPAddress.TryParse(endpoint.Host, out var endpointAddress) &&
            endpointAddress.AddressFamily == AddressFamily.InterNetworkV6)
        {
            throw Failure(
                ErrorCodes.Ipv6Unsupported,
                "endpoint-validation",
                "This POC supports IPv4 switch endpoints only.");
        }

        var commands = commandIds.Select(profile.GetRequiredCommand).DistinctBy(static item => item.Id).ToArray();
        var startedAt = _timeProvider.GetUtcNow();
        await using var transport = _transportFactory.Create();
        var negotiator = new TelnetNegotiator();
        var authenticated = false;
        using var sessionCancellation = CreateStageCancellation(cancellationToken, _options.Timeouts.Session);
        var sessionToken = sessionCancellation.Token;

        try
        {
            await ConnectAsync(transport, endpoint, sessionToken).ConfigureAwait(false);
            await AuthenticateAsync(transport, negotiator, credentials, profile.Telnet, sessionToken).ConfigureAwait(false);
            authenticated = true;

            var outputs = new List<CommandOutput>(commands.Length);
            foreach (var command in commands)
            {
                sessionToken.ThrowIfCancellationRequested();
                await WriteLineWithTimeoutAsync(
                        transport,
                        command.Command,
                        ErrorCodes.CommandTimeout,
                        "command-write",
                        sessionToken)
                    .ConfigureAwait(false);
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
                        sessionToken)
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
        catch (OperationCanceledException exception) when (sessionCancellation.IsCancellationRequested)
        {
            throw Failure(
                ErrorCodes.CommandTimeout,
                "telnet-session",
                "The Telnet session exceeded its total time limit.",
                true,
                exception);
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
            await WriteLineWithTimeoutAsync(
                    transport,
                    credentials.Username,
                    ErrorCodes.PromptParseFailed,
                    "authentication-write",
                    cancellationToken)
                .ConfigureAwait(false);
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

        await WriteLineWithTimeoutAsync(
                transport,
                credentials.Password,
                ErrorCodes.PromptParseFailed,
                "authentication-write",
                cancellationToken)
            .ConfigureAwait(false);
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
        var receivedTextBytes = 0;
        var receivedWireBytes = 0;
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

                receivedWireBytes += read;
                if (receivedWireBytes > _options.MaximumWireBytes)
                {
                    throw Failure(
                        ErrorCodes.OutputLimitExceeded,
                        stage,
                        "The switch response exceeded the configured wire-byte safety limit.");
                }

                var frame = negotiator.Process(buffer.AsSpan(0, read), _options.MaximumNegotiationBytesWithoutText);
                if (frame.Responses.Length > 0)
                {
                    await transport.WriteAsync(frame.Responses, stageCancellation.Token).ConfigureAwait(false);
                }

                receivedTextBytes += frame.Text.Length;
                if (receivedTextBytes > _options.MaximumOutputBytes)
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

                await RemovePagingMarkersAsync(
                        transport,
                        visibleOutput,
                        pagingMarkers,
                        pagingContinueByte,
                        stageCancellation.Token)
                    .ConfigureAwait(false);

                var visibleText = OutputNormalizer.CleanControlCharacters(GetDetectionTail(visibleOutput));
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
            await WriteLineWithTimeoutAsync(
                    transport,
                    logoutCommand,
                    ErrorCodes.PromptParseFailed,
                    "logout",
                    logoutCancellation.Token)
                .ConfigureAwait(false);
        }
        catch
        {
            // The connection is closed in the caller's finally block. Logout
            // failures must never hide the original collection result.
        }
    }

    private async Task WriteLineWithTimeoutAsync(
        IByteTransport transport,
        string value,
        string timeoutCode,
        string stage,
        CancellationToken cancellationToken)
    {
        var bytes = WireEncoding.GetBytes(value + "\r\n");
        using var writeCancellation = CreateStageCancellation(cancellationToken, _options.Timeouts.Write);
        try
        {
            await transport.WriteAsync(bytes, writeCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw Failure(timeoutCode, stage, $"The {stage} stage timed out.", true, exception);
        }
    }

    private async Task RemovePagingMarkersAsync(
        IByteTransport transport,
        StringBuilder visibleOutput,
        IReadOnlyList<string> pagingMarkers,
        byte pagingContinueByte,
        CancellationToken cancellationToken)
    {
        if (pagingMarkers.Count == 0 || visibleOutput.Length == 0)
        {
            return;
        }

        var longestMarker = pagingMarkers.Max(static marker => marker.Length);
        while (true)
        {
            var start = Math.Max(0, visibleOutput.Length - _options.DetectionWindowCharacters - longestMarker);
            var tail = visibleOutput.ToString(start, visibleOutput.Length - start);
            var selectedIndex = -1;
            var selectedLength = 0;
            foreach (var marker in pagingMarkers)
            {
                var index = tail.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index >= 0 && (selectedIndex < 0 || index < selectedIndex))
                {
                    selectedIndex = index;
                    selectedLength = marker.Length;
                }
            }

            if (selectedIndex < 0)
            {
                return;
            }

            visibleOutput.Remove(start + selectedIndex, selectedLength);
            await transport.WriteAsync(new byte[] { pagingContinueByte }, cancellationToken).ConfigureAwait(false);
        }
    }

    private string GetDetectionTail(StringBuilder builder)
    {
        var start = Math.Max(0, builder.Length - _options.DetectionWindowCharacters);
        return builder.ToString(start, builder.Length - start);
    }

    private static Regex CreateRegex(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static CancellationTokenSource CreateStageCancellation(CancellationToken outer, TimeSpan timeout)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(outer);
        source.CancelAfter(timeout);
        return source;
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
