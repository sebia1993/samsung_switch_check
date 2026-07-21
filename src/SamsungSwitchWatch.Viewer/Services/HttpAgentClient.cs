using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Services;

public sealed class HttpAgentClient : IAgentClient
{
    private readonly ViewerSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly HubConnection _hub;
    private readonly IReadOnlyList<byte[]> _acceptedCertificatePins;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _reconnectSync = new();
    private IReadOnlyDictionary<string, string> _deviceNames =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    private Task? _reconnectTask;
    private int _certificatePinRejected;
    private int _apiCompatibility;
    private bool _disposed;

    public HttpAgentClient(ViewerSettings settings)
    {
        _settings = ViewerSettingsSanitizer.Sanitize(settings);
        _settings.BearerToken = settings.BearerToken;
        if (!ViewerSettingsSanitizer.IsValidForLiveConnection(_settings, out var reason))
        {
            throw new InvalidOperationException(reason);
        }
        _acceptedCertificatePins = _settings.AcceptedCertificateFingerprints
            .Select(Convert.FromHexString)
            .ToArray();

        _httpClient = new HttpClient(CreatePinnedHandler())
        {
            BaseAddress = new Uri(_settings.AgentUri),
            Timeout = TimeSpan.FromSeconds(20)
        };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _settings.BearerToken);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SamsungSwitchWatch.Viewer/2.0");

        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(_settings.AgentUri), "/hubs/events"), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_settings.BearerToken);
                options.HttpMessageHandlerFactory = _ => CreatePinnedHandler();
                options.WebSocketConfiguration = webSocketOptions =>
                    webSocketOptions.RemoteCertificateValidationCallback = ValidateWebSocketCertificate;
            })
            .WithAutomaticReconnect(new IndefiniteReconnectPolicy())
            .Build();

        _hub.On<JsonElement>("eventChanged", payload =>
        {
            var names = Volatile.Read(ref _deviceNames);
            var mapped = AgentContractMapper.MapEventChange(payload, names);
            if (mapped is not null) EventChanged?.Invoke(this, mapped);
        });
        _hub.Reconnecting += _ =>
        {
            ConnectionStateChanged?.Invoke(this, AgentConnectionState.Reconnecting);
            return Task.CompletedTask;
        };
        _hub.Reconnected += _ =>
        {
            ConnectionStateChanged?.Invoke(this, AgentConnectionState.Connected);
            return Task.CompletedTask;
        };
        _hub.Closed += exception =>
        {
            if (!_lifetime.IsCancellationRequested)
            {
                var failure = exception is null
                    ? null
                    : AgentClientErrors.Translate(exception, ConsumePinRejection());
                var state = failure?.SuggestedConnectionState == AgentConnectionState.NeedsPairing
                    ? AgentConnectionState.NeedsPairing
                    : AgentConnectionState.Offline;
                ConnectionStateChanged?.Invoke(this, state);
                if (state != AgentConnectionState.NeedsPairing) EnsureReconnectLoop();
            }
            return Task.CompletedTask;
        };
    }

    public event EventHandler<AgentEventChangeDto>? EventChanged;
    public event EventHandler<AgentConnectionState>? ConnectionStateChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_hub.State is HubConnectionState.Connected or HubConnectionState.Connecting or HubConnectionState.Reconnecting)
            {
                return;
            }

            ConnectionStateChanged?.Invoke(this, AgentConnectionState.Connecting);
            try
            {
                Interlocked.Exchange(ref _certificatePinRejected, 0);
                await _hub.StartAsync(cancellationToken).ConfigureAwait(false);
                ConnectionStateChanged?.Invoke(this, AgentConnectionState.Connected);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                var failure = AgentClientErrors.Translate(exception, ConsumePinRejection());
                var state = failure.SuggestedConnectionState == AgentConnectionState.NeedsPairing
                    ? AgentConnectionState.NeedsPairing
                    : AgentConnectionState.Offline;
                ConnectionStateChanged?.Invoke(this, state);
                if (state != AgentConnectionState.NeedsPairing) EnsureReconnectLoop();
                throw failure;
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var preferred = await SendPreferredGetAsync(
            AgentApiRoutes.SnapshotV3,
            AgentApiRoutes.SnapshotV2,
            cancellationToken).ConfigureAwait(false);
        using var response = preferred.Response;
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        AgentSnapshotDto snapshot;
        try
        {
            snapshot = preferred.ApiVersion == 3
                ? AgentContractMapper.MapSnapshotV3(json)
                : AgentContractMapper.MapSnapshotV2(json);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { throw AgentClientErrors.Translate(exception); }
        var names = snapshot.Devices.ToDictionary(item => item.Id, item => item.Name, StringComparer.OrdinalIgnoreCase);
        Volatile.Write(ref _deviceNames, new ReadOnlyDictionary<string, string>(names));
        return snapshot;
    }

    public async Task<IReadOnlyList<SwitchEventDto>> GetRecentEventsAsync(int limit, CancellationToken cancellationToken)
    {
        var preferred = await SendPreferredGetAsync(
            AgentApiRoutes.RecentEventsV3(limit),
            AgentApiRoutes.RecentEventsV2(limit),
            cancellationToken).ConfigureAwait(false);
        using var response = preferred.Response;
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try { return AgentContractMapper.MapEvents(json, Volatile.Read(ref _deviceNames)); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { throw AgentClientErrors.Translate(exception); }
    }

    public async Task<EventChangePageDto> GetEventChangesAsync(long cursor, int limit, CancellationToken cancellationToken)
    {
        var preferred = await SendPreferredGetAsync(
            AgentApiRoutes.EventChangesV3(cursor, limit),
            AgentApiRoutes.EventChangesV2(cursor, limit),
            cancellationToken).ConfigureAwait(false);
        using var response = preferred.Response;
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try { return AgentContractMapper.MapEventChangePage(json, Volatile.Read(ref _deviceNames)); }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        { throw AgentClientErrors.Translate(exception); }
    }

    public async Task<CommandResultDto> ExecuteRegisteredCheckAsync(
        string deviceId,
        string commandId,
        CancellationToken cancellationToken)
    {
        HttpResponseMessage response;
        var apiVersion = Volatile.Read(ref _apiCompatibility);
        if (apiVersion != 2)
        {
            var body = JsonSerializer.Serialize(new
            {
                deviceIds = new[] { deviceId },
                commandIds = new[] { commandId }
            });
            response = await SendRawAsync(HttpMethod.Post, AgentApiRoutes.CheckRunsV3, body, cancellationToken).ConfigureAwait(false);
            if (ApiCompatibilityPolicy.ShouldFallback(response.StatusCode))
            {
                response.Dispose();
                Interlocked.Exchange(ref _apiCompatibility, 2);
                response = await SendAsync(HttpMethod.Post, AgentApiRoutes.Command(deviceId, commandId), cancellationToken).ConfigureAwait(false);
                apiVersion = 2;
            }
            else
            {
                await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _apiCompatibility, 3);
                apiVersion = 3;
            }
        }
        else
        {
            response = await SendAsync(HttpMethod.Post, AgentApiRoutes.Command(deviceId, commandId), cancellationToken).ConfigureAwait(false);
        }
        using (response)
        {
            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (apiVersion == 3)
                {
                    var devices = root.TryGetProperty("devices", out var deviceResults)
                                  && deviceResults.ValueKind == JsonValueKind.Array
                        ? deviceResults
                        : default;
                    var selected = devices.ValueKind == JsonValueKind.Array && devices.GetArrayLength() > 0
                        ? devices[0]
                        : default;
                    var success = selected.ValueKind == JsonValueKind.Object
                                  && selected.TryGetProperty("success", out var successValue)
                                  && successValue.ValueKind == JsonValueKind.True;
                    var errorCode = selected.ValueKind == JsonValueKind.Object
                        && selected.TryGetProperty("errorCode", out var errorValue)
                        ? errorValue.GetString()
                        : null;
                    return new CommandResultDto(success,
                        success ? "등록된 다중 장비 점검이 완료되었습니다." : $"점검 실패 · {errorCode ?? "CHECK_RUN_FAILED"}",
                        errorCode);
                }
                var collectorStatus = root.TryGetProperty("collectorStatus", out var status) ? status.GetString() ?? "OK" : "OK";
                var eventsCreated = root.TryGetProperty("eventsCreated", out var events) && events.TryGetInt32(out var count) ? count : 0;
                return new CommandResultDto(true, $"등록 점검 완료 · {collectorStatus} · 이벤트 {eventsCreated}건");
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            { throw AgentClientErrors.Translate(exception); }
        }
    }

    public async Task<bool> AcknowledgeAsync(string eventId, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, AgentApiRoutes.Acknowledge(eventId), cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void EnsureReconnectLoop()
    {
        lock (_reconnectSync)
        {
            if (_disposed || _reconnectTask is { IsCompleted: false }) return;
            _reconnectTask = Task.Run(() => ReconnectLoopAsync(_lifetime.Token));
        }
    }

    private async Task ReconnectLoopAsync(CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var delay = ReconnectDelay.GetDelay(attempt++);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            try
            {
                await StartAsync(cancellationToken).ConfigureAwait(false);
                if (_hub.State == HubConnectionState.Connected) return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // StartAsync reports the offline state. Keep retrying until disposal.
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string route,
        CancellationToken cancellationToken)
    {
        var response = await SendRawAsync(method, route, null, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private async Task<(HttpResponseMessage Response, int ApiVersion)> SendPreferredGetAsync(
        string v3Route,
        string v2Route,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _apiCompatibility) == 2)
        {
            return (await SendAsync(HttpMethod.Get, v2Route, cancellationToken).ConfigureAwait(false), 2);
        }

        var response = await SendRawAsync(HttpMethod.Get, v3Route, null, cancellationToken).ConfigureAwait(false);
        if (ApiCompatibilityPolicy.ShouldFallback(response.StatusCode))
        {
            response.Dispose();
            Interlocked.Exchange(ref _apiCompatibility, 2);
            return (await SendAsync(HttpMethod.Get, v2Route, cancellationToken).ConfigureAwait(false), 2);
        }

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        Interlocked.Exchange(ref _apiCompatibility, 3);
        return (response, 3);
    }

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method,
        string route,
        string? jsonBody,
        CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _certificatePinRejected, 0);
        try
        {
            using var request = new HttpRequestMessage(method, route);
            if (jsonBody is not null)
            {
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            }
            var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw AgentClientErrors.Translate(exception, ConsumePinRejection());
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        string body;
        try { body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false); }
        catch
        {
            response.Dispose();
            throw;
        }
        var typed = AgentClientErrors.FromStatus(response.StatusCode, body);
        response.Dispose();
        throw typed;
    }

    private HttpClientHandler CreatePinnedHandler() => new()
    {
        ServerCertificateCustomValidationCallback = ValidateCertificate
    };

    private bool ValidateCertificate(HttpRequestMessage _, X509Certificate2? certificate, X509Chain? __, SslPolicyErrors ___)
        => ValidateCertificateCore(certificate);

    private bool ValidateWebSocketCertificate(object _, X509Certificate? certificate, X509Chain? __, SslPolicyErrors ___)
    {
        if (certificate is X509Certificate2 certificate2) return ValidateCertificateCore(certificate2);
        if (certificate is null) return ValidateCertificateCore(null);
        using var copy = new X509Certificate2(certificate);
        return ValidateCertificateCore(copy);
    }

    private bool ValidateCertificateCore(X509Certificate2? certificate)
    {
        if (certificate is null)
        {
            Interlocked.Exchange(ref _certificatePinRejected, 1);
            return false;
        }
        var actual = SHA256.HashData(certificate.RawData);
        var accepted = CertificatePinMatcher.Matches(actual, _acceptedCertificatePins);
        if (!accepted) Interlocked.Exchange(ref _certificatePinRejected, 1);
        return accepted;
    }

    private bool ConsumePinRejection() => Interlocked.Exchange(ref _certificatePinRejected, 0) != 0;

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();

        Task? reconnect;
        lock (_reconnectSync) reconnect = _reconnectTask;
        if (reconnect is not null)
        {
            try { await reconnect.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch (Exception exception) when (exception is OperationCanceledException or TimeoutException) { }
        }

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await _hub.StopAsync(shutdown.Token).ConfigureAwait(false); } catch { }
        try { await _hub.DisposeAsync().ConfigureAwait(false); } catch { }
        _httpClient.Dispose();
        _startGate.Dispose();
        _lifetime.Dispose();
    }
}

internal static class CertificatePinMatcher
{
    public static bool Matches(ReadOnlySpan<byte> actualSha256, IReadOnlyList<byte[]> acceptedPins)
    {
        var accepted = false;
        foreach (var pin in acceptedPins)
        {
            // Do not return early: compare every configured pin so timing does not
            // reveal which rotation slot matched.
            accepted |= pin.Length == actualSha256.Length
                        && CryptographicOperations.FixedTimeEquals(actualSha256, pin);
        }
        return accepted;
    }
}

internal static class ApiCompatibilityPolicy
{
    public static bool ShouldFallback(HttpStatusCode statusCode) => statusCode == HttpStatusCode.NotFound;
}

internal sealed class IndefiniteReconnectPolicy : IRetryPolicy
{
    public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
        ReconnectDelay.GetDelay(checked((int)Math.Min(retryContext.PreviousRetryCount, int.MaxValue)));
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
