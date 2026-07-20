using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Services;

public sealed class HttpAgentClient : IAgentClient
{
    private readonly ViewerSettings _settings;
    private readonly HttpClient _httpClient;
    private readonly HubConnection _hub;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _reconnectSync = new();
    private IReadOnlyDictionary<string, string> _deviceNames =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
    private Task? _reconnectTask;
    private bool _disposed;

    public HttpAgentClient(ViewerSettings settings)
    {
        _settings = ViewerSettingsSanitizer.Sanitize(settings);
        _settings.BearerToken = settings.BearerToken;
        if (!ViewerSettingsSanitizer.IsValidForLiveConnection(_settings, out var reason))
        {
            throw new InvalidOperationException(reason);
        }

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
            ConnectionStateChanged?.Invoke(this, AgentConnectionState.Connecting);
            return Task.CompletedTask;
        };
        _hub.Reconnected += _ =>
        {
            ConnectionStateChanged?.Invoke(this, AgentConnectionState.Connected);
            return Task.CompletedTask;
        };
        _hub.Closed += _ =>
        {
            if (!_lifetime.IsCancellationRequested)
            {
                ConnectionStateChanged?.Invoke(this, AgentConnectionState.Offline);
                EnsureReconnectLoop();
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
                await _hub.StartAsync(cancellationToken).ConfigureAwait(false);
                ConnectionStateChanged?.Invoke(this, AgentConnectionState.Connected);
            }
            catch
            {
                ConnectionStateChanged?.Invoke(this, AgentConnectionState.Offline);
                EnsureReconnectLoop();
                throw;
            }
        }
        finally
        {
            _startGate.Release();
        }
    }

    public async Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(AgentApiRoutes.SnapshotV2, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = AgentContractMapper.MapSnapshotV2(json);
        var names = snapshot.Devices.ToDictionary(item => item.Id, item => item.Name, StringComparer.OrdinalIgnoreCase);
        Volatile.Write(ref _deviceNames, new ReadOnlyDictionary<string, string>(names));
        return snapshot;
    }

    public async Task<IReadOnlyList<SwitchEventDto>> GetRecentEventsAsync(int limit, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(AgentApiRoutes.RecentEventsV2(limit), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return AgentContractMapper.MapEvents(json, Volatile.Read(ref _deviceNames));
    }

    public async Task<EventChangePageDto> GetEventChangesAsync(long cursor, int limit, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(AgentApiRoutes.EventChangesV2(cursor, limit), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return AgentContractMapper.MapEventChangePage(json, Volatile.Read(ref _deviceNames));
    }

    public async Task<CommandResultDto> ExecuteRegisteredCheckAsync(
        string deviceId,
        string commandId,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync(AgentApiRoutes.Command(deviceId, commandId), null, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return new CommandResultDto(false, "등록된 점검 요청이 거부되었습니다.", ExtractErrorCode(json) ?? response.StatusCode.ToString());
        }
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var collectorStatus = root.TryGetProperty("collectorStatus", out var status) ? status.GetString() ?? "OK" : "OK";
        var eventsCreated = root.TryGetProperty("eventsCreated", out var events) && events.TryGetInt32(out var count) ? count : 0;
        return new CommandResultDto(true, $"등록 점검 완료 · {collectorStatus} · 이벤트 {eventsCreated}건");
    }

    public async Task<bool> AcknowledgeAsync(string eventId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync(AgentApiRoutes.Acknowledge(eventId), null, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
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

    private static string? ExtractErrorCode(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("error").GetProperty("code").GetString();
        }
        catch
        {
            return null;
        }
    }

    private HttpClientHandler CreatePinnedHandler() => new()
    {
        ServerCertificateCustomValidationCallback = ValidateCertificate
    };

    private bool ValidateCertificate(HttpRequestMessage _, X509Certificate2? certificate, X509Chain? __, SslPolicyErrors ___)
    {
        if (certificate is null) return false;
        var actual = SHA256.HashData(certificate.RawData);
        var expected = Convert.FromHexString(_settings.CertificateFingerprint);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

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
