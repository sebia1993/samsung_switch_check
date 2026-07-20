using System.Text.Json;
using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Services;

public static class AgentApiRoutes
{
    public const string Status = "/api/v1/status";
    public const string Devices = "/api/v1/devices";
    public static string EventsAfter(long sequence) => $"/api/v1/events?after={Math.Max(0, sequence)}";
    public static string Command(string deviceId, string commandId) =>
        $"/api/v1/commands/{Uri.EscapeDataString(deviceId)}/{Uri.EscapeDataString(commandId)}";
    public static string Acknowledge(string eventId) => $"/api/v1/events/{Uri.EscapeDataString(eventId)}/ack";
}

public static class AgentContractMapper
{
    public static AgentSnapshotDto MapSnapshot(string statusJson, string devicesJson)
    {
        using var status = JsonDocument.Parse(statusJson);
        using var devices = JsonDocument.Parse(devicesJson);
        return MapSnapshot(status.RootElement, devices.RootElement);
    }

    public static IReadOnlyList<SwitchEventDto> MapEvents(
        string eventsJson,
        IReadOnlyDictionary<string, string>? deviceNames = null)
    {
        using var events = JsonDocument.Parse(eventsJson);
        if (events.RootElement.ValueKind != JsonValueKind.Array) return [];
        return events.RootElement.EnumerateArray()
            .Select(item => MapEvent(item, deviceNames))
            .Where(item => item is not null)
            .Cast<SwitchEventDto>()
            .OrderBy(item => item.Sequence)
            .ToArray();
    }

    internal static AgentSnapshotDto MapSnapshot(JsonElement status, JsonElement devices)
    {
        var generatedAt = DateTimeValue(status, "utc") ?? DateTimeOffset.UtcNow;
        var connected = BoolValue(status, "connected") ?? false;
        var mockMode = BoolValue(status, "mockMode") ?? false;
        var activeCritical = IntValue(status, "activeCritical") ?? 0;
        var lastSequence = LongValue(status, "lastEventSequence") ?? 0;
        var agentId = StringValue(status, "agentId") ?? "agent";
        var mappedDevices = new List<DeviceSnapshotDto>();

        if (devices.ValueKind == JsonValueKind.Array)
        {
            foreach (var device in devices.EnumerateArray())
            {
                mappedDevices.Add(MapDevice(device, generatedAt, activeCritical, mappedDevices.Count == 0));
            }
        }

        return new AgentSnapshotDto(
            generatedAt,
            connected ? AgentConnectionState.Connected : AgentConnectionState.Disconnected,
            mappedDevices,
            Math.Max(0, lastSequence),
            $"Agent {agentId}",
            $"{(mockMode ? "모의" : "실환경")} · 장비 {mappedDevices.Count}대 · 원문 미전송");
    }

    internal static SwitchEventDto? MapEvent(JsonElement item, IReadOnlyDictionary<string, string>? deviceNames = null)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var sequence = LongValue(item, "sequence") ?? 0;
        var id = StringValue(item, "id") ?? string.Empty;
        var deviceId = StringValue(item, "deviceId") ?? string.Empty;
        if (sequence <= 0 || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(deviceId)) return null;

        var severityText = StringValue(item, "severity") ?? "Info";
        var stateText = StringValue(item, "state") ?? "New";
        var recovered = stateText.Equals("Recovered", StringComparison.OrdinalIgnoreCase)
                        || severityText.Equals("Recovery", StringComparison.OrdinalIgnoreCase);
        var acknowledged = recovered || stateText.Equals("Acknowledged", StringComparison.OrdinalIgnoreCase)
                          || DateTimeValue(item, "acknowledgedUtc").HasValue;
        var severity = recovered ? DeviceHealth.Normal : severityText.ToUpperInvariant() switch
        {
            "CRITICAL" => DeviceHealth.Critical,
            "WARNING" => DeviceHealth.Warning,
            "RECOVERY" => DeviceHealth.Normal,
            _ => DeviceHealth.Normal
        };

        var name = deviceNames is not null && deviceNames.TryGetValue(deviceId, out var displayName)
            ? displayName
            : deviceId;
        return new SwitchEventDto(
            sequence,
            id,
            deviceId,
            name,
            DateTimeValue(item, "occurredUtc") ?? DateTimeOffset.UtcNow,
            severity,
            StringValue(item, "type") ?? "event",
            StringValue(item, "title") ?? "Switch event",
            StringValue(item, "message") ?? string.Empty,
            acknowledged,
            recovered,
            StringValue(item, "conditionKey"));
    }

    private static DeviceSnapshotDto MapDevice(JsonElement device, DateTimeOffset generatedAt, int activeCritical, bool firstDevice)
    {
        var id = StringValue(device, "id") ?? "unknown";
        var name = StringValue(device, "displayName") ?? id;
        var model = StringValue(device, "model") ?? "Unknown";
        var uplinkPort = StringValue(device, "uplinkPort") ?? "-";
        var collections = device.TryGetProperty("collection", out var collection) && collection.ValueKind == JsonValueKind.Array
            ? collection.EnumerateArray().ToArray()
            : [];
        var latestCapture = collections.Select(item => DateTimeValue(item, "capturedUtc"))
            .Where(item => item.HasValue).Select(item => item!.Value).DefaultIfEmpty().Max();
        var hasCapture = latestCapture != default;

        var interfaces = FindCollection(collections, "interface_status");
        var system = FindCollection(collections, "system");
        var version = FindCollection(collections, "version");
        var collectorHealth = FindCollection(collections, "collector_health");
        var uplinkUp = DataBool(interfaces, "uplinkOperationalUp");
        var portsUp = DataLong(interfaces, "portsUp");
        var portsDown = DataLong(interfaces, "portsDown");
        var uptimeSeconds = DataLong(system, "uptimeSeconds");
        var softwareVersion = DataString(version, "softwareVersion") ?? "-";
        var post = DataString(system, "post") ?? PostSummary(system);
        var collectorCode = DataString(collectorHealth, "errorCode") ?? collections.Select(item => DataString(item, "collectorStatus"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && !value.Equals("OK", StringComparison.OrdinalIgnoreCase));

        var health = !hasCapture ? DeviceHealth.Loading
            : collectorCode is not null ? DeviceHealth.Disconnected
            : (firstDevice && activeCritical > 0) || uplinkUp == false ? DeviceHealth.Critical
            : generatedAt - latestCapture > TimeSpan.FromMinutes(10) ? DeviceHealth.Warning
            : DeviceHealth.Normal;
        var summary = health switch
        {
            DeviceHealth.Loading => "첫 수집 결과를 기다리는 중",
            DeviceHealth.Disconnected => $"스위치 수집 실패 · {collectorCode}",
            DeviceHealth.Critical => $"중요 업링크 포트 {uplinkPort} 장애",
            DeviceHealth.Warning => "마지막 수집 결과가 오래되었습니다.",
            _ => "등록된 모든 점검 정상"
        };

        var metrics = new List<DeviceMetricDto>
        {
            new("업링크", uplinkUp is null ? "확인 중" : $"포트 {uplinkPort} · {(uplinkUp.Value ? "UP" : "DOWN")}", uplinkUp is null ? DeviceHealth.Loading : uplinkUp.Value ? DeviceHealth.Normal : DeviceHealth.Critical),
            new("포트 요약", portsUp.HasValue ? $"UP {portsUp} · DOWN {portsDown ?? 0}" : "데이터 없음", portsDown > 0 ? DeviceHealth.Warning : DeviceHealth.Normal),
            new("소프트웨어", softwareVersion),
            new("POST", post ?? "데이터 없음", post is not null && !post.Equals("PASS", StringComparison.OrdinalIgnoreCase) ? DeviceHealth.Warning : DeviceHealth.Normal),
            new("수집기", collectorCode ?? "OK", collectorCode is null ? DeviceHealth.Normal : DeviceHealth.Disconnected)
        };

        return new DeviceSnapshotDto(
            id,
            name,
            model,
            "장비 주소는 Agent에만 보관",
            health,
            hasCapture ? latestCapture : generatedAt,
            summary,
            FormatUptime(uptimeSeconds),
            metrics);
    }

    private static JsonElement? FindCollection(JsonElement[] items, string commandId) => items
        .Where(item => string.Equals(StringValue(item, "commandId"), commandId, StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(item => DateTimeValue(item, "capturedUtc"))
        .Cast<JsonElement?>()
        .FirstOrDefault();

    private static JsonElement? Data(JsonElement? collection)
    {
        if (collection is { } item && item.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) return data;
        return null;
    }

    private static bool? DataBool(JsonElement? item, string property) => Data(item) is { } data ? BoolValue(data, property) : null;
    private static long? DataLong(JsonElement? item, string property) => Data(item) is { } data ? LongValue(data, property) : null;
    private static string? DataString(JsonElement? item, string property) => Data(item) is { } data ? StringValue(data, property) : null;

    private static string? PostSummary(JsonElement? system)
    {
        if (Data(system) is not { } data || !data.TryGetProperty("postChecks", out var checks) || checks.ValueKind != JsonValueKind.Object) return null;
        return checks.EnumerateObject().All(item => item.Value.GetString()?.Equals("PASS", StringComparison.OrdinalIgnoreCase) == true) ? "PASS" : "CHECK";
    }

    private static string FormatUptime(long? seconds)
    {
        if (!seconds.HasValue || seconds < 0) return "-";
        var value = TimeSpan.FromSeconds(seconds.Value);
        return $"{(int)value.TotalDays}일 {value.Hours:00}:{value.Minutes:00}";
    }

    private static string? StringValue(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool? BoolValue(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static long? LongValue(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.TryGetInt64(out var result) ? result : null;

    private static int? IntValue(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static DateTimeOffset? DateTimeValue(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetDateTimeOffset(out var result) ? result : null;
}
