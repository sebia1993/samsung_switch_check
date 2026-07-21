using System.Text.Json;
using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Services;

public static class AgentApiRoutes
{
    public const string Status = "/api/v1/status";
    public const string Devices = "/api/v1/devices";
    public static string EventsAfter(long sequence) => $"/api/v1/events?after={Math.Max(0, sequence)}";
    public const string SnapshotV2 = "/api/v2/snapshot";
    public const string SnapshotV3 = "/api/v3/snapshot";
    public static string RecentEventsV2(int limit) => $"/api/v2/events/recent?limit={Math.Clamp(limit, 1, 500)}";
    public static string RecentEventsV3(int limit) => $"/api/v3/events/recent?limit={Math.Clamp(limit, 1, 500)}";
    public static string EventChangesV2(long cursor, int limit) =>
        $"/api/v2/events/changes?after={Math.Max(0, cursor)}&limit={Math.Clamp(limit, 1, 500)}";
    public static string EventChangesV3(long cursor, int limit) =>
        $"/api/v3/events/changes?after={Math.Max(0, cursor)}&limit={Math.Clamp(limit, 1, 500)}";
    public const string CheckRunsV3 = "/api/v3/check-runs";
    public const string CertificateStatus = "/api/v1/certificate/fingerprint";
    public static string Command(string deviceId, string commandId) =>
        $"/api/v1/commands/{Uri.EscapeDataString(deviceId)}/{Uri.EscapeDataString(commandId)}";
    public static string Acknowledge(string eventId) => $"/api/v1/events/{Uri.EscapeDataString(eventId)}/ack";
}

public static class AgentContractMapper
{
    public static AgentSnapshotDto MapSnapshotV3(string snapshotJson)
    {
        using var document = JsonDocument.Parse(snapshotJson);
        return MapSnapshotV3(document.RootElement);
    }

    public static AgentSnapshotDto MapSnapshotV2(string snapshotJson)
    {
        using var document = JsonDocument.Parse(snapshotJson);
        var root = document.RootElement;
        var devices = root.TryGetProperty("devices", out var value) ? value : default;
        return MapSnapshotV2(root, devices);
    }

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
        var items = events.RootElement.ValueKind == JsonValueKind.Array
            ? events.RootElement
            : events.RootElement.TryGetProperty("events", out var nested) ? nested : default;
        if (items.ValueKind != JsonValueKind.Array) return [];
        return items.EnumerateArray()
            .Select(item => MapEvent(item, deviceNames))
            .Where(item => item is not null)
            .Cast<SwitchEventDto>()
            .OrderBy(item => item.Sequence)
            .ToArray();
    }

    public static EventChangePageDto MapEventChangePage(
        string changesJson,
        IReadOnlyDictionary<string, string>? deviceNames = null)
    {
        using var document = JsonDocument.Parse(changesJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new EventChangePageDto(0, 0, false, []);
        }

        var mapped = new List<AgentEventChangeDto>();
        if (root.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in changes.EnumerateArray())
            {
                var change = MapEventChange(item, deviceNames);
                if (change is not null) mapped.Add(change);
            }
        }

        mapped.Sort((left, right) => left.ChangeSequence.CompareTo(right.ChangeSequence));
        var maximum = mapped.Count == 0 ? 0 : mapped[^1].ChangeSequence;
        var highWatermark = Math.Max(maximum, LongValue(root, "highWatermark") ?? 0);
        var nextCursor = Math.Max(maximum, LongValue(root, "nextCursor") ?? 0);
        var hasMore = BoolValue(root, "hasMore") ?? nextCursor < highWatermark;
        var resetRequired = BoolValue(root, "resetRequired") ?? false;
        var resetCursor = Math.Max(0, LongValue(root, "resetCursor") ?? highWatermark);
        return new EventChangePageDto(highWatermark, nextCursor, hasMore, mapped, resetRequired, resetCursor);
    }

    internal static AgentEventChangeDto? MapEventChange(
        JsonElement item,
        IReadOnlyDictionary<string, string>? deviceNames = null)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        var sequence = LongValue(item, "changeSequence") ?? 0;
        if (sequence <= 0 || !item.TryGetProperty("event", out var eventElement)) return null;
        var mappedEvent = MapEvent(eventElement, deviceNames);
        return mappedEvent is null
            ? null
            : new AgentEventChangeDto(sequence, StringValue(item, "changeKind") ?? "Changed", mappedEvent);
    }

    internal static AgentSnapshotDto MapSnapshotV3(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || IntValue(root, "apiVersion") != 3)
        {
            throw new JsonException("AGENT_V3_CONTRACT_INVALID");
        }

        var generatedAt = DateTimeValue(root, "utc") ?? DateTimeOffset.UtcNow;
        var mockMode = BoolValue(root, "mockMode") ?? false;
        var agentId = StringValue(root, "agentId") ?? "agent";
        var counts = ObjectValue(root, "counts");
        var channels = ObjectValue(root, "channels");
        var agentChannel = ObjectValue(channels, "agent");
        var apiChannel = ObjectValue(channels, "api");
        var realtimeChannel = ObjectValue(channels, "realtime");
        var readiness = ObjectValue(channels, "readiness");
        var storage = ObjectValue(channels, "storage");
        var certificate = ObjectValue(channels, "certificate");
        var agentStatus = StringValue(agentChannel, "status") ?? "unknown";
        var apiStatus = StringValue(apiChannel, "status") ?? "unknown";
        var realtimeStatus = StringValue(realtimeChannel, "status") ?? "unknown";
        var readinessStatus = StringValue(readiness, "status") ?? "not-ready";
        var readinessCode = StringValue(readiness, "code")
                            ?? (readinessStatus.Equals("ready", StringComparison.OrdinalIgnoreCase)
                                ? "READY"
                                : "AGENT_NOT_READY");
        var ready = readinessStatus.Equals("ready", StringComparison.OrdinalIgnoreCase);
        var connected = agentStatus.Equals("connected", StringComparison.OrdinalIgnoreCase)
                        && apiStatus.Equals("available", StringComparison.OrdinalIgnoreCase);
        var activeCritical = IntValue(counts, "activeCritical") ?? 0;
        var unacknowledged = LongValue(counts, "unacknowledged") ?? 0;
        var highWatermark = LongValue(counts, "eventChangeHighWatermark")
                            ?? LongValue(counts, "lastSequence")
                            ?? 0;
        var maxConcurrentDevices = IntValue(root, "maxConcurrentDevices") ?? 1;
        var storageReady = BoolValue(storage, "ready");
        var storageCode = StringValue(storage, "errorCode");
        var storageSchemaVersion = IntValue(storage, "schemaVersion");
        var certificateStatus = StringValue(certificate, "state") ?? "unknown";
        var certificateExpiresAt = DateTimeValue(certificate, "notAfterUtc");
        var mappedDevices = new List<DeviceSnapshotDto>();
        if (root.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array)
        {
            foreach (var device in devices.EnumerateArray())
            {
                mappedDevices.Add(MapDevice(device, generatedAt, 0, false));
            }
        }

        var state = mockMode ? AgentConnectionState.Demo
            : !connected ? AgentConnectionState.Offline
            : !ready ? AgentConnectionState.Stale
            : AgentConnectionState.Connected;
        var operational = BuildOperationalStatuses(
            state,
            agentStatus,
            apiStatus,
            realtimeStatus,
            readinessCode,
            maxConcurrentDevices,
            storageReady,
            storageCode,
            storageSchemaVersion,
            certificateStatus,
            certificateExpiresAt);
        return new AgentSnapshotDto(
            generatedAt,
            state,
            mappedDevices,
            Math.Max(0, highWatermark),
            $"Agent {agentId} · API v3",
            $"{(mockMode ? "모의" : "실환경")} · 장비 {mappedDevices.Count}대 · 활성 장애 {activeCritical}건 · {readinessCode}",
            agentId,
            ready,
            readinessCode,
            Math.Max(0, unacknowledged),
            3,
            agentStatus,
            apiStatus,
            realtimeStatus,
            certificateStatus,
            certificateExpiresAt,
            operational,
            MaxConcurrentDevices: Math.Max(1, maxConcurrentDevices));
    }

    public static AgentSnapshotDto WithCertificateStatus(AgentSnapshotDto snapshot, string certificateJson)
    {
        using var document = JsonDocument.Parse(certificateJson);
        var root = document.RootElement;
        var enabled = BoolValue(root, "httpsEnabled") ?? false;
        var state = StringValue(root, "state") ?? (enabled ? "unknown" : "disabled");
        var expiresAt = DateTimeValue(root, "notAfterUtc");
        var statuses = (snapshot.OperationalStatuses ?? []).ToList();
        statuses.RemoveAll(item => item.Code.StartsWith("CERT_", StringComparison.Ordinal));
        var health = state.ToUpperInvariant() switch
        {
            "EXPIRED" or "UNAVAILABLE" => DeviceHealth.Critical,
            "EXPIRING" => DeviceHealth.Warning,
            "ACTIVE" or "VALID" => DeviceHealth.Normal,
            _ when enabled => DeviceHealth.Normal,
            _ => DeviceHealth.Warning
        };
        var code = state.ToUpperInvariant() switch
        {
            "EXPIRED" => "CERT_EXPIRED",
            "EXPIRING" => "CERT_EXPIRING",
            "UNAVAILABLE" => "CERT_UNAVAILABLE",
            _ when enabled => "CERT_VALID",
            _ => "CERT_DISABLED"
        };
        var detail = expiresAt is null
            ? $"HTTPS 인증서 상태 {state}"
            : $"HTTPS 인증서 상태 {state} · 만료 {expiresAt.Value.LocalDateTime:yyyy-MM-dd}";
        statuses.Add(new OperationalStatusDto(code, "HTTPS 인증서", detail, health));
        return snapshot with
        {
            CertificateStatus = state,
            CertificateExpiresAt = expiresAt,
            OperationalStatuses = statuses
        };
    }

    private static IReadOnlyList<OperationalStatusDto> BuildOperationalStatuses(
        AgentConnectionState state,
        string agentStatus,
        string apiStatus,
        string realtimeStatus,
        string readinessCode,
        int maxConcurrentDevices,
        bool? storageReady = null,
        string? storageCode = null,
        int? storageSchemaVersion = null,
        string certificateStatus = "unknown",
        DateTimeOffset? certificateExpiresAt = null)
    {
        var statuses = new List<OperationalStatusDto>
        {
            new("AGENT_CHANNEL", "원격 수집기", $"Agent 채널 {agentStatus}",
                state is AgentConnectionState.Offline or AgentConnectionState.NeedsPairing
                    ? DeviceHealth.Disconnected
                    : DeviceHealth.Normal),
            new("API_CHANNEL", "HTTPS API", $"API 채널 {apiStatus}",
                apiStatus.Equals("available", StringComparison.OrdinalIgnoreCase)
                    ? DeviceHealth.Normal
                    : DeviceHealth.Critical),
            new(realtimeStatus.Equals("available", StringComparison.OrdinalIgnoreCase)
                    ? "REALTIME_AVAILABLE"
                    : "REALTIME_DEGRADED",
                "실시간 이벤트",
                realtimeStatus.Equals("available", StringComparison.OrdinalIgnoreCase)
                    ? "SignalR 실시간 채널 정상"
                    : $"SignalR 채널 {realtimeStatus} · HTTPS 캐치업 유지",
                realtimeStatus.Equals("available", StringComparison.OrdinalIgnoreCase)
                    ? DeviceHealth.Normal
                    : DeviceHealth.Warning),
            new("POLLING", "수집 진행", $"동시 장비 수집 상한 {Math.Max(1, maxConcurrentDevices)}대", DeviceHealth.Normal)
        };

        if (storageReady.HasValue)
        {
            statuses.Add(new OperationalStatusDto(
                storageReady.Value ? "DB_READY" : "DB_INTEGRITY_FAILED",
                "로컬 상태 DB",
                storageReady.Value
                    ? $"Readiness 정상 · 스키마 v{storageSchemaVersion?.ToString() ?? "-"}"
                    : $"Liveness 유지 · Readiness 실패 · {storageCode ?? "STORAGE_WRITE_FAILED"}",
                storageReady.Value ? DeviceHealth.Normal : DeviceHealth.Critical));
        }

        if (!certificateStatus.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            var certificateHealth = certificateStatus.ToUpperInvariant() switch
            {
                "EXPIRED" or "UNAVAILABLE" => DeviceHealth.Critical,
                "EXPIRING" => DeviceHealth.Warning,
                _ => DeviceHealth.Normal
            };
            statuses.Add(new OperationalStatusDto(
                certificateHealth == DeviceHealth.Critical ? "CERT_UNAVAILABLE"
                    : certificateHealth == DeviceHealth.Warning ? "CERT_EXPIRING" : "CERT_VALID",
                "HTTPS 인증서",
                certificateExpiresAt is null
                    ? $"인증서 상태 {certificateStatus}"
                    : $"인증서 상태 {certificateStatus} · 만료 {certificateExpiresAt.Value.LocalDateTime:yyyy-MM-dd}",
                certificateHealth));
        }

        if (!readinessCode.Equals("READY", StringComparison.OrdinalIgnoreCase)
            && !readinessCode.Equals("ready", StringComparison.OrdinalIgnoreCase))
        {
            var databaseFailure = readinessCode.Contains("STORAGE", StringComparison.OrdinalIgnoreCase)
                                  || readinessCode.Contains("DB_", StringComparison.OrdinalIgnoreCase);
            if (!databaseFailure || !storageReady.HasValue)
            {
                statuses.Add(new OperationalStatusDto(
                    databaseFailure ? "DB_INTEGRITY_FAILED" : readinessCode,
                    databaseFailure ? "DB 준비 상태 실패" : "Agent 준비 상태",
                    databaseFailure
                        ? $"Liveness 유지 · Readiness 실패 · {readinessCode}"
                        : $"현재 상태 미확인 · {readinessCode}",
                    databaseFailure ? DeviceHealth.Critical : DeviceHealth.Warning));
            }
        }
        return statuses;
    }

    internal static AgentSnapshotDto MapSnapshotV2(JsonElement root, JsonElement devices)
    {
        var generatedAt = DateTimeValue(root, "utc") ?? DateTimeOffset.UtcNow;
        var connected = BoolValue(root, "connected") ?? false;
        var ready = BoolValue(root, "ready") ?? false;
        var mockMode = BoolValue(root, "mockMode") ?? false;
        var activeCritical = IntValue(root, "activeCritical") ?? 0;
        var unacknowledged = LongValue(root, "unacknowledged") ?? 0;
        var highWatermark = LongValue(root, "highWatermark") ?? 0;
        var agentId = StringValue(root, "agentId") ?? "agent";
        var readinessCode = StringValue(root, "readinessCode") ?? (ready ? "READY" : "AGENT_NOT_READY");
        var mappedDevices = new List<DeviceSnapshotDto>();

        if (devices.ValueKind == JsonValueKind.Array)
        {
            foreach (var device in devices.EnumerateArray())
            {
                mappedDevices.Add(MapDevice(device, generatedAt, activeCritical, mappedDevices.Count == 0));
            }
        }

        var state = !connected ? AgentConnectionState.Offline
            : !ready ? AgentConnectionState.Stale
            : AgentConnectionState.Connected;
        return new AgentSnapshotDto(
            generatedAt,
            state,
            mappedDevices,
            Math.Max(0, highWatermark),
            $"Agent {agentId}",
            $"{(mockMode ? "모의" : "실환경")} · 장비 {mappedDevices.Count}대 · {readinessCode}",
            agentId,
            ready,
            readinessCode,
            Math.Max(0, unacknowledged),
            2,
            connected ? "connected" : "offline",
            connected ? "available" : "unavailable",
            "unknown",
            OperationalStatuses: BuildOperationalStatuses(
                state,
                connected ? "connected" : "offline",
                connected ? "available" : "unavailable",
                "unknown",
                readinessCode,
                1));
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
            connected ? AgentConnectionState.Connected : AgentConnectionState.Offline,
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
            StringValue(item, "conditionKey"),
            BoolValue(item, "isActiveCondition") ?? (!recovered && severity == DeviceHealth.Critical),
            DateTimeValue(item, "recoveredUtc"));
    }

    private static DeviceSnapshotDto MapDevice(JsonElement device, DateTimeOffset generatedAt, int activeCritical, bool firstDevice)
    {
        var id = StringValue(device, "id") ?? "unknown";
        var name = StringValue(device, "displayName") ?? id;
        var model = StringValue(device, "model") ?? "Unknown";
        var uplinkPort = StringValue(device, "uplinkPort") ?? "-";
        var collection = device.TryGetProperty("collections", out var v3Collections)
                         && v3Collections.ValueKind == JsonValueKind.Array
            ? v3Collections
            : device.TryGetProperty("collection", out var legacyCollections)
              && legacyCollections.ValueKind == JsonValueKind.Array
                ? legacyCollections
                : default;
        var collections = collection.ValueKind == JsonValueKind.Array ? collection.EnumerateArray().ToArray() : [];
        var latestCapture = collections.Select(item => DateTimeValue(item, "capturedUtc"))
            .Where(item => item.HasValue).Select(item => item!.Value).DefaultIfEmpty().Max();
        if (latestCapture == default) latestCapture = DateTimeValue(device, "lastCollectionUtc") ?? default;
        var hasCapture = latestCapture != default;

        var interfaces = FindCollection(collections, "interface_status");
        var system = FindCollection(collections, "system");
        var version = FindCollection(collections, "version");
        var collectorHealth = FindCollection(collections, "collector_health");
        var collectionHealth = ObjectValue(device, "collectionHealth");
        var uplinkUp = DataBool(interfaces, "uplinkOperationalUp");
        var portsUp = DataLong(interfaces, "portsUp");
        var portsDown = DataLong(interfaces, "portsDown");
        var uptimeSeconds = DataLong(system, "uptimeSeconds");
        var softwareVersion = DataString(version, "softwareVersion") ?? "-";
        var post = DataString(system, "post") ?? PostSummary(system);
        var reportedCollectorState = StringValue(collectionHealth, "state")
                                     ?? DataString(collectorHealth, "state");
        string collectorState = reportedCollectorState ?? "Initializing";
        var collectorCode = StringValue(collectionHealth, "errorCode")
                            ?? DataString(collectorHealth, "errorCode")
                            ?? collections.Select(item => DataString(item, "collectorStatus"))
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value) && !value.Equals("OK", StringComparison.OrdinalIgnoreCase));
        var collectorDegraded = string.Equals(collectorState, "Degraded", StringComparison.OrdinalIgnoreCase);
        var collectorUnsupported = string.Equals(collectorState, "Unsupported", StringComparison.OrdinalIgnoreCase);
        var hasActiveCriticalEvent = firstDevice && activeCritical > 0;
        var collectorFailed = string.Equals(collectorState, "Failed", StringComparison.OrdinalIgnoreCase)
                              || string.Equals(collectorState, "AuthBlocked", StringComparison.OrdinalIgnoreCase)
                              || (reportedCollectorState is null && collectorCode is not null);

        var capabilities = new List<CollectorCapabilityDto>();
        if (device.TryGetProperty("capabilities", out var capabilityItems)
            && capabilityItems.ValueKind == JsonValueKind.Array)
        {
            foreach (var capability in capabilityItems.EnumerateArray())
            {
                var commandId = StringValue(capability, "commandId");
                if (string.IsNullOrWhiteSpace(commandId)) continue;
                var candidates = capability.TryGetProperty("candidateClis", out var candidateItems)
                                 && candidateItems.ValueKind == JsonValueKind.Array
                    ? candidateItems.EnumerateArray()
                        .Where(item => item.ValueKind == JsonValueKind.String)
                        .Select(item => item.GetString())
                        .Where(item => !string.IsNullOrWhiteSpace(item))
                        .Cast<string>()
                        .Take(8)
                        .ToArray()
                    : [];
                capabilities.Add(new CollectorCapabilityDto(
                    commandId,
                    BoolValue(capability, "supported") ?? false,
                    StringValue(capability, "state") ?? "Unknown",
                    StringValue(capability, "errorCode"),
                    StringValue(capability, "primaryCli") ?? StringValue(capability, "cli"),
                    StringValue(capability, "selectedCli"),
                    candidates,
                    StringValue(capability, "lastSuccessfulCli")));
            }
        }

        var health = !hasCapture ? hasActiveCriticalEvent ? DeviceHealth.Critical : DeviceHealth.Loading
            : collectorFailed ? DeviceHealth.Disconnected
            : uplinkUp == false ? DeviceHealth.Critical
            : hasActiveCriticalEvent ? DeviceHealth.Critical
            : collectorDegraded || collectorUnsupported ? DeviceHealth.Warning
            : generatedAt - latestCapture > TimeSpan.FromMinutes(10) ? DeviceHealth.Warning
            : DeviceHealth.Normal;
        var summary = collectorFailed
            ? $"스위치 수집 실패 · {collectorCode}"
            : uplinkUp == false
                ? $"중요 업링크 포트 {uplinkPort} 장애"
            : hasActiveCriticalEvent
                ? $"활성 장애 이벤트 {activeCritical}건"
            : collectorUnsupported
                ? $"일부 명령을 지원하지 않음 · {collectorCode}"
            : collectorDegraded
                ? $"일시적 수집 불안정 · {collectorCode}"
            : health switch
            {
                DeviceHealth.Loading => "첫 수집 결과를 기다리는 중",
                DeviceHealth.Warning => "마지막 수집 결과가 오래되었습니다.",
                _ => "등록된 모든 점검 정상"
            };

        var metrics = new List<DeviceMetricDto>
        {
            new("업링크", uplinkUp is null ? "확인 중" : $"포트 {uplinkPort} · {(uplinkUp.Value ? "UP" : "DOWN")}", uplinkUp is null ? DeviceHealth.Loading : uplinkUp.Value ? DeviceHealth.Normal : DeviceHealth.Critical),
            new("포트 요약", portsUp.HasValue ? $"UP {portsUp} · DOWN {portsDown ?? 0}" : "데이터 없음", portsDown > 0 ? DeviceHealth.Warning : DeviceHealth.Normal),
            new("소프트웨어", softwareVersion),
            new("POST", post ?? "데이터 없음", post is not null && !post.Equals("PASS", StringComparison.OrdinalIgnoreCase) ? DeviceHealth.Warning : DeviceHealth.Normal),
            new("수집기", collectorCode ?? collectorState, collectorFailed ? DeviceHealth.Disconnected : collectorDegraded || collectorUnsupported ? DeviceHealth.Warning : DeviceHealth.Normal),
            new("수집 기능", capabilities.Count == 0 ? "확인 중" : $"{capabilities.Count(item => item.Supported)}/{capabilities.Count} 지원",
                capabilities.Any(item => !item.Supported) ? DeviceHealth.Warning : DeviceHealth.Normal)
        };
        if (hasActiveCriticalEvent)
        {
            metrics.Add(new DeviceMetricDto("활성 장애", $"{activeCritical}건", DeviceHealth.Critical));
        }

        return new DeviceSnapshotDto(
            id,
            name,
            model,
            "장비 주소는 Agent에만 보관",
            health,
            hasCapture ? latestCapture : generatedAt,
            summary,
            FormatUptime(uptimeSeconds),
            metrics,
            capabilities,
            collectorState,
            collectorCode);
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
        var values = checks.EnumerateObject().Select(item => item.Value.GetString()).ToArray();
        return values.Length == 0
            ? null
            : values.All(value => value?.Equals("PASS", StringComparison.OrdinalIgnoreCase) == true) ? "PASS" : "CHECK";
    }

    private static string FormatUptime(long? seconds)
    {
        if (!seconds.HasValue || seconds < 0) return "-";
        var value = TimeSpan.FromSeconds(seconds.Value);
        return $"{(int)value.TotalDays}일 {value.Hours:00}:{value.Minutes:00}";
    }

    private static string? StringValue(JsonElement item, string property)
    {
        if (item.ValueKind != JsonValueKind.Object) return null;
        if (!item.TryGetProperty(property, out var value)) return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static bool? BoolValue(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(property, out var value)
        && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;

    private static long? LongValue(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(property, out var value)
        && value.TryGetInt64(out var result) ? result : null;

    private static int? IntValue(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(property, out var value)
        && value.TryGetInt32(out var result) ? result : null;

    private static DateTimeOffset? DateTimeValue(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && value.TryGetDateTimeOffset(out var result) ? result : null;

    private static JsonElement ObjectValue(JsonElement item, string property) =>
        item.ValueKind == JsonValueKind.Object
        && item.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Object
            ? value
            : default;
}
