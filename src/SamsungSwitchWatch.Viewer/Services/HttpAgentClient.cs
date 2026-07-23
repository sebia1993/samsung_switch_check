using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SamsungSwitchWatch.Core.Profiles;
using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Services;

public sealed class HttpAgentClient : IAgentClient
{
    internal static readonly TimeSpan ReadOnlyQueryTimeout = TimeSpan.FromSeconds(510);
    private static readonly TimeSpan ControlRequestTimeout = TimeSpan.FromSeconds(20);
    internal const int MaximumIdentityResponseBytes = 32 * 1024;
    internal const int MaximumErrorResponseBytes = 64 * 1024;
    // Eight 64-KiB UTF-8 outputs still fit when every byte requires a six-byte
    // JSON escape, with room left for the v4 envelope and command metadata.
    internal const int MaximumTelnetResponseBytes = 4 * 1024 * 1024;
    private const int MaximumBoundedResponseBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private readonly ViewerSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly HttpClient _queryHttpClient;
    private readonly CertificatePinValidator _certificateValidator;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private AgentIdentityDto? _identity;
    private int _identityValidationReady;
    private int _connectionState = (int)AgentConnectionState.NeedsConnection;
    private bool _disposed;

    public HttpAgentClient(ViewerSettings settings) : this(settings, null, null, null)
    {
    }

    internal HttpAgentClient(
        ViewerSettings settings,
        HttpMessageHandler? controlHandler,
        HttpMessageHandler? queryHandler,
        CertificatePinValidator? certificateValidator)
    {
        var clean = ViewerSettingsSanitizer.Sanitize(settings);
        if (!ViewerSettingsSanitizer.IsValidForLiveConnection(clean, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

        settings.AgentUri = clean.AgentUri;
        settings.AgentTrustPins = clean.AgentTrustPins;
        _settings = settings;
        _certificateValidator = certificateValidator ?? new CertificatePinValidator(_settings);
        _httpClient = new HttpClient(controlHandler ?? CreatePinnedHttpHandler(_certificateValidator))
        {
            BaseAddress = new Uri(clean.AgentUri),
            Timeout = ControlRequestTimeout
        };
        _queryHttpClient = new HttpClient(queryHandler ?? CreatePinnedHttpHandler(_certificateValidator))
        {
            BaseAddress = new Uri(clean.AgentUri),
            Timeout = ReadOnlyQueryTimeout
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SamsungSwitchWatch.Viewer/0.8");
        _queryHttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SamsungSwitchWatch.Viewer/0.8");
    }

    public event EventHandler<AgentEventChangeDto>? EventChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<AgentConnectionState>? ConnectionStateChanged;

    public bool SupportsStatelessV4 => true;

    public Task StartAsync(CancellationToken cancellationToken) =>
        StartCoreAsync(forceIdentityRefresh: true, cancellationToken);

    private async Task StartCoreAsync(
        bool forceIdentityRefresh,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceIdentityRefresh && HasValidatedIdentity())
            {
                return;
            }

            // A failed explicit refresh must not leave an older identity
            // eligible for later Telnet requests.
            Volatile.Write(ref _identityValidationReady, 0);
            using var requestCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            requestCancellation.CancelAfter(ControlRequestTimeout);
            var currentState = (AgentConnectionState)Volatile.Read(ref _connectionState);
            if (currentState != AgentConnectionState.Connected)
            {
                PublishConnectionState(currentState == AgentConnectionState.Offline
                    ? AgentConnectionState.Reconnecting
                    : AgentConnectionState.Connecting);
            }

            using var response = await SendAsync(
                _httpClient,
                HttpMethod.Get,
                AgentApiRoutes.IdentityV4,
                null,
                requestCancellation.Token).ConfigureAwait(false);
            var json = await ReadBoundedUtf8Async(
                    response.Content,
                    MaximumIdentityResponseBytes,
                    requestCancellation.Token)
                .ConfigureAwait(false);
            var identity = AgentContractMapper.MapIdentityV4(json);
            if (!_certificateValidator.CompleteTrust(identity.CertificatePublicKeySha256))
            {
                PublishConnectionState(AgentConnectionState.Stale);
                throw new AgentClientException("AGENT_IDENTITY_CHANGED", AgentConnectionState.Stale);
            }
            Volatile.Write(ref _identity, identity);
            Volatile.Write(ref _identityValidationReady, 1);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            var typed = new AgentClientException(
                "AGENT_TIMEOUT",
                AgentConnectionState.Offline,
                exception);
            PublishConnectionState(typed.SuggestedConnectionState);
            throw typed;
        }
        catch (AgentClientException)
        {
            throw;
        }
        catch (Exception exception)
        {
            var typed = AgentClientErrors.Translate(exception);
            PublishConnectionState(typed.SuggestedConnectionState);
            throw typed;
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<AgentIdentityDto> GetIdentityAsync(CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return Volatile.Read(ref _identity)!;
    }

    public async Task<TelnetExecutionResultDto> TestTelnetAsync(
        TelnetTargetDto target,
        CancellationToken cancellationToken)
    {
        ValidateTarget(target.Host, target.Port, target.Model);
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await SendTelnetAsync(
                AgentApiRoutes.TelnetTestV4,
                target,
                target.RequestId,
                [],
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<TelnetExecutionResultDto> ExecuteTelnetAsync(
        TelnetExecuteRequestDto request,
        CancellationToken cancellationToken)
    {
        ValidateTarget(request.Host, request.Port, request.Model);
        var normalizedCommands = NormalizeCommands(request.Commands);
        var normalizedRequest = request with { Commands = normalizedCommands };
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await SendTelnetAsync(
                AgentApiRoutes.TelnetExecuteV4,
                normalizedRequest,
                request.RequestId,
                normalizedCommands,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var identity = await GetIdentityAsync(cancellationToken).ConfigureAwait(false);
        return new AgentSnapshotDto(
            DateTimeOffset.UtcNow,
            AgentConnectionState.Connected,
            [],
            0,
            $"Agent {identity.AgentId} · API v4",
            "Viewer 주도형 Telnet 중계 준비",
            identity.AgentId,
            ApiVersion: 4,
            AgentChannelStatus: "connected",
            ApiChannelStatus: "available",
            RealtimeChannelStatus: "viewer-local",
            OperationalStatuses:
            [
                new("HTTPS_TOFU", "Agent HTTPS", "인증서 공개키를 Viewer가 자동 확인합니다.", DeviceHealth.Normal),
                new("STATELESS_AGENT", "장비 정보 보관", "장비와 계정은 Viewer에만 저장됩니다.", DeviceHealth.Normal)
            ],
            ReadOnlyQueriesEnabled: true,
            ReadOnlyQueryMaxCommandLength: 128,
            ReadOnlyQueryMaxOutputBytes: identity.MaxOutputBytes);
    }

    public Task<IReadOnlyList<SwitchEventDto>> GetRecentEventsAsync(int limit, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SwitchEventDto>>([]);

    public Task<EventChangePageDto> GetEventChangesAsync(long cursor, int limit, CancellationToken cancellationToken) =>
        Task.FromResult(new EventChangePageDto(cursor, cursor, false, []));

    public Task<CommandResultDto> ExecuteRegisteredCheckAsync(
        string deviceId,
        string commandId,
        CancellationToken cancellationToken) =>
        Task.FromResult(new CommandResultDto(false, "Viewer 장비 정보가 필요합니다.", "VIEWER_DEVICE_NOT_FOUND"));

    public Task<ReadOnlyQueryResultDto> ExecuteReadOnlyQueryAsync(
        string deviceId,
        string command,
        CancellationToken cancellationToken) =>
        Task.FromException<ReadOnlyQueryResultDto>(new AgentClientException(
            "VIEWER_DEVICE_NOT_FOUND", AgentConnectionState.Stale));

    public Task<bool> AcknowledgeAsync(string eventId, CancellationToken cancellationToken) =>
        Task.FromResult(false);

    private async Task<TelnetExecutionResultDto> SendTelnetAsync(
        string route,
        object body,
        string expectedRequestId,
        IReadOnlyList<string> expectedCommands,
        CancellationToken cancellationToken)
    {
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        using var requestCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCancellation.CancelAfter(ReadOnlyQueryTimeout);
        try
        {
            using var response = await SendAsync(
                _queryHttpClient,
                HttpMethod.Post,
                route,
                jsonBytes,
                requestCancellation.Token).ConfigureAwait(false);
            try
            {
                var resultJson = await ReadBoundedUtf8Async(
                        response.Content,
                        MaximumTelnetResponseBytes,
                        requestCancellation.Token)
                    .ConfigureAwait(false);
                var maximumOutputBytes = Math.Min(
                    _identity?.MaxOutputBytes ?? ReadOnlyQueryPolicy.MaximumOutputBytes,
                    ReadOnlyQueryPolicy.MaximumOutputBytes);
                return AgentContractMapper.MapTelnetExecutionResultV4(
                    resultJson,
                    expectedRequestId,
                    expectedCommands,
                    maximumOutputBytes);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                var typed = new AgentClientException(
                    "AGENT_TIMEOUT",
                    AgentConnectionState.Offline,
                    exception);
                PublishConnectionState(typed.SuggestedConnectionState);
                throw typed;
            }
            catch (AgentClientException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var typed = AgentClientErrors.Translate(exception);
                PublishConnectionState(typed.SuggestedConnectionState);
                throw typed;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(jsonBytes);
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        if (HasValidatedIdentity())
        {
            return;
        }

        await StartCoreAsync(forceIdentityRefresh: false, cancellationToken).ConfigureAwait(false);
    }

    private bool HasValidatedIdentity() =>
        Volatile.Read(ref _identityValidationReady) == 1
        && Volatile.Read(ref _identity) is not null
        && (AgentConnectionState)Volatile.Read(ref _connectionState)
        == AgentConnectionState.Connected;

    private async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string route,
        byte[]? jsonBody,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, route);
            if (jsonBody is not null)
            {
                request.Content = new ByteArrayContent(jsonBody);
                request.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")
                    {
                        CharSet = "utf-8"
                    };
            }
            var response = await client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            PublishConnectionState(AgentConnectionState.Connected);
            if (response.IsSuccessStatusCode) return response;
            var statusCode = response.StatusCode;
            string body;
            try
            {
                body = await ReadBoundedUtf8Async(
                        response.Content,
                        MaximumErrorResponseBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                response.Dispose();
            }
            var typed = AgentClientErrors.FromStatus(statusCode, body);
            throw typed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (AgentClientException)
        {
            // Receiving an HTTP response proves that the Agent transport is reachable.
            // Application-level failures remain request errors rather than link outages.
            throw;
        }
        catch (Exception exception)
        {
            if (_certificateValidator.IdentityChanged)
            {
                PublishConnectionState(AgentConnectionState.Stale);
                throw new AgentClientException("AGENT_IDENTITY_CHANGED", AgentConnectionState.Stale, exception);
            }
            var typed = AgentClientErrors.Translate(exception);
            PublishConnectionState(typed.SuggestedConnectionState);
            throw typed;
        }
    }

    private void PublishConnectionState(AgentConnectionState state)
    {
        var previous = (AgentConnectionState)Interlocked.Exchange(ref _connectionState, (int)state);
        if (previous != state)
        {
            ConnectionStateChanged?.Invoke(this, state);
        }
    }

    private static void ValidateTarget(string host, int port, string model)
    {
        var draft = new ManagedDeviceDraft
        {
            DisplayName = "validation",
            Host = host,
            Model = model,
            Username = "validation",
            Password = "validation"
        };
        if (port != 23 || !ManagedDeviceValidator.TryValidate(draft, true, out _))
        {
            throw new AgentClientException("VIEWER_DEVICE_INVALID", AgentConnectionState.Stale);
        }
    }

    private static IReadOnlyList<string> NormalizeCommands(IReadOnlyList<string>? commands)
    {
        if (commands is not { Count: > 0 and <= 8 })
        {
            throw new AgentClientException("QUERY_COMMAND_BLOCKED", AgentConnectionState.Stale);
        }

        var normalized = new string[commands.Count];
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < commands.Count; index++)
        {
            var validation = ReadOnlyQueryPolicy.Validate(commands[index]);
            if (!validation.IsAllowed || !unique.Add(validation.NormalizedCommand!))
            {
                throw new AgentClientException("QUERY_COMMAND_BLOCKED", AgentConnectionState.Stale);
            }

            normalized[index] = validation.NormalizedCommand!;
        }

        return normalized;
    }

    internal static async Task<string> ReadBoundedUtf8Async(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maximumBytes is < 1 or > MaximumBoundedResponseBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        if (content.Headers.ContentLength is long contentLength
            && contentLength > maximumBytes)
        {
            throw new InvalidDataException("AGENT_RESPONSE_TOO_LARGE");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(checked(maximumBytes + 1));
        var bytesRead = 0;
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            while (bytesRead <= maximumBytes)
            {
                var read = await stream.ReadAsync(
                        buffer.AsMemory(bytesRead, maximumBytes + 1 - bytesRead),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                bytesRead += read;
                if (bytesRead > maximumBytes)
                {
                    throw new InvalidDataException("AGENT_RESPONSE_TOO_LARGE");
                }
            }

            try
            {
                return StrictUtf8.GetString(buffer, 0, bytesRead);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("AGENT_RESPONSE_UTF8_INVALID", exception);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer.AsSpan(0, Math.Min(bytesRead, buffer.Length)));
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static HttpClientHandler CreateDirectHttpHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseDefaultCredentials = false
    };

    internal static HttpClientHandler CreatePinnedHttpHandler(CertificatePinValidator validator)
    {
        var handler = CreateDirectHttpHandler();
        handler.ServerCertificateCustomValidationCallback = validator.Validate;
        return handler;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        Volatile.Write(ref _identityValidationReady, 0);
        _httpClient.Dispose();
        _queryHttpClient.Dispose();
        _startGate.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class CertificatePinValidator
{
    private readonly ViewerSettings _settings;
    private int _identityChanged;
    private string? _observedPin;

    public CertificatePinValidator(ViewerSettings settings) => _settings = settings;

    public bool IdentityChanged => Volatile.Read(ref _identityChanged) != 0;

    public bool Validate(
        HttpRequestMessage request,
        X509Certificate2? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (certificate is null) return false;
        string pin;
        try { pin = GetSpkiSha256(certificate); }
        catch (CryptographicException) { return false; }

        Volatile.Write(ref _observedPin, pin);
        if (!_settings.TryGetAgentTrustPin(out var expected)) return true;
        var matches = FixedTimeEquals(expected, pin);
        if (!matches)
        {
            Interlocked.Exchange(ref _identityChanged, 1);
        }
        return matches;
    }

    public bool CompleteTrust(string identityPin)
    {
        var observedPin = Volatile.Read(ref _observedPin);
        if (observedPin is null || !FixedTimeEquals(observedPin, identityPin))
        {
            Interlocked.Exchange(ref _identityChanged, 1);
            return false;
        }
        if (_settings.TryGetAgentTrustPin(out var expected) && !FixedTimeEquals(expected, identityPin))
        {
            Interlocked.Exchange(ref _identityChanged, 1);
            return false;
        }
        _settings.SetAgentTrustPin(identityPin.ToUpperInvariant());
        return true;
    }

    public static string GetSpkiSha256(X509Certificate2 certificate)
    {
        byte[] spki;
        using (var ecdsa = certificate.GetECDsaPublicKey())
        {
            if (ecdsa is not null)
            {
                spki = ecdsa.ExportSubjectPublicKeyInfo();
                return Convert.ToHexString(SHA256.HashData(spki));
            }
        }
        using (var rsa = certificate.GetRSAPublicKey())
        {
            if (rsa is null) throw new CryptographicException("CERTIFICATE_PUBLIC_KEY_UNSUPPORTED");
            spki = rsa.ExportSubjectPublicKeyInfo();
        }
        return Convert.ToHexString(SHA256.HashData(spki));
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != right.Length) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left.ToUpperInvariant()),
            Encoding.ASCII.GetBytes(right.ToUpperInvariant()));
    }
}

internal static class ApiCompatibilityPolicy
{
    public static bool ShouldFallback(HttpStatusCode statusCode) => statusCode == HttpStatusCode.NotFound;
}

internal sealed class DirectWebProxy : IWebProxy
{
    public static DirectWebProxy Instance { get; } = new();
    private DirectWebProxy() { }
    public ICredentials? Credentials { get => null; set { } }
    public Uri GetProxy(Uri destination) => destination;
    public bool IsBypassed(Uri host) => true;
}

internal static class ReconnectDelay
{
    private static readonly int[] Seconds = [0, 2, 5, 15, 30, 60];

    public static TimeSpan GetDelay(int attempt, double? jitter = null)
    {
        var seconds = Seconds[Math.Clamp(attempt, 0, Seconds.Length - 1)];
        if (seconds == 0) return TimeSpan.Zero;
        var factor = jitter ?? (0.85 + (Random.Shared.NextDouble() * 0.3));
        return TimeSpan.FromSeconds(Math.Min(60, seconds * Math.Clamp(factor, 0.85, 1.15)));
    }
}
