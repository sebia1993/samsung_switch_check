using System.Net.Http.Headers;
using System.Net.Http;
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
    private readonly Dictionary<string, string> _deviceNames = new(StringComparer.OrdinalIgnoreCase);
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
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("SamsungSwitchWatch.Viewer/1.0");

        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(new Uri(_settings.AgentUri), "/hubs/events"), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_settings.BearerToken);
                options.HttpMessageHandlerFactory = _ => CreatePinnedHandler();
            })
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15)])
            .Build();

        _hub.On<JsonElement>("eventReceived", payload =>
        {
            var mapped = AgentContractMapper.MapEvent(payload, _deviceNames);
            if (mapped is not null) EventReceived?.Invoke(this, mapped);
        });
        _hub.On<JsonElement>("eventUpdated", payload =>
        {
            var mapped = AgentContractMapper.MapEvent(payload, _deviceNames);
            if (mapped is not null) EventUpdated?.Invoke(this, mapped);
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
            ConnectionStateChanged?.Invoke(this, AgentConnectionState.Disconnected);
            return Task.CompletedTask;
        };
    }

    public event EventHandler<SwitchEventDto>? EventReceived;
    public event EventHandler<SwitchEventDto>? EventUpdated;
    public event EventHandler<AgentConnectionState>? ConnectionStateChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ConnectionStateChanged?.Invoke(this, AgentConnectionState.Connecting);
        await _hub.StartAsync(cancellationToken);
        ConnectionStateChanged?.Invoke(this, AgentConnectionState.Connected);
    }

    public async Task<AgentSnapshotDto> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        using var statusResponse = await _httpClient.GetAsync(AgentApiRoutes.Status, cancellationToken);
        statusResponse.EnsureSuccessStatusCode();
        using var devicesResponse = await _httpClient.GetAsync(AgentApiRoutes.Devices, cancellationToken);
        devicesResponse.EnsureSuccessStatusCode();
        var statusJson = await statusResponse.Content.ReadAsStringAsync(cancellationToken);
        var devicesJson = await devicesResponse.Content.ReadAsStringAsync(cancellationToken);
        var snapshot = AgentContractMapper.MapSnapshot(statusJson, devicesJson);
        _deviceNames.Clear();
        foreach (var device in snapshot.Devices) _deviceNames[device.Id] = device.Name;
        return snapshot;
    }

    public async Task<IReadOnlyList<SwitchEventDto>> GetEventsAfterAsync(long sequence, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(AgentApiRoutes.EventsAfter(sequence), cancellationToken);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return AgentContractMapper.MapEvents(json, _deviceNames);
    }

    public async Task<CommandResultDto> ExecuteRegisteredCheckAsync(string deviceId, string commandId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync(AgentApiRoutes.Command(deviceId, commandId), null, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return new CommandResultDto(false, "점검 요청이 거부되었습니다.", ExtractErrorCode(json) ?? response.StatusCode.ToString());
        }
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var collectorStatus = root.TryGetProperty("collectorStatus", out var status) ? status.GetString() ?? "OK" : "OK";
        var eventsCreated = root.TryGetProperty("eventsCreated", out var events) && events.TryGetInt32(out var count) ? count : 0;
        return new CommandResultDto(true, $"등록 점검 완료 · {collectorStatus} · 이벤트 {eventsCreated}건");
    }

    public async Task<bool> AcknowledgeAsync(string eventId, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsync(AgentApiRoutes.Acknowledge(eventId), null, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static string? ExtractErrorCode(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.GetProperty("error").GetProperty("code").GetString();
        }
        catch { return null; }
    }

    private HttpClientHandler CreatePinnedHandler() => new()
    {
        ServerCertificateCustomValidationCallback = ValidateCertificate
    };

    private bool ValidateCertificate(HttpRequestMessage _, X509Certificate2? certificate, X509Chain? __, SslPolicyErrors ___)
    {
        if (certificate is null) return false;
        var actual = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(actual),
            Convert.FromHexString(_settings.CertificateFingerprint));
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try { await _hub.DisposeAsync(); } catch { }
        _httpClient.Dispose();
    }
}
