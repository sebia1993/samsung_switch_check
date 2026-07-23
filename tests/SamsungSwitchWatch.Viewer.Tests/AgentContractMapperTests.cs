using System.Text.Json;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class AgentContractMapperTests
{
    private const string StatusJson = """
    {
      "agentId": "agent-poc-01",
      "connected": true,
      "mockMode": false,
      "deviceCount": 1,
      "activeCritical": 1,
      "unacknowledged": 2,
      "lastEventSequence": 42,
      "lastCollectionUtc": "2026-07-20T08:30:00+00:00",
      "utc": "2026-07-20T08:31:00+00:00"
    }
    """;

    private const string DevicesJson = """
    [
      {
        "id": "TEST-SW-01",
        "displayName": "ACCESS-SW-01",
        "model": "IES4224GP",
        "uplinkPort": "24",
        "collection": [
          {
            "commandId": "version",
            "capturedUtc": "2026-07-20T08:30:00+00:00",
            "data": { "model": "IES4224GP", "softwareVersion": "1.2.3", "mainPower": "normal" }
          },
          {
            "commandId": "system",
            "capturedUtc": "2026-07-20T08:30:00+00:00",
            "data": { "uptimeSeconds": 183900, "postChecks": { "RAM": "PASS", "FLASH": "PASS" } }
          },
          {
            "commandId": "interface_status",
            "capturedUtc": "2026-07-20T08:30:00+00:00",
            "data": { "uplinkPort": "24", "uplinkAdminUp": true, "uplinkOperationalUp": false, "portsUp": 23, "portsDown": 1 }
          }
        ]
      }
    ]
    """;

    private const string EventsJson = """
    [
      {
        "sequence": 42,
        "id": "evt-42",
        "deviceId": "TEST-SW-01",
        "severity": "Critical",
        "type": "uplink-down",
        "title": "Uplink down",
        "message": "Port 24 operational state changed: UP -> DOWN.",
        "state": "New",
        "occurredUtc": "2026-07-20T08:30:10+00:00",
        "acknowledgedUtc": null,
        "recoveredUtc": null,
        "conditionKey": "uplink:24",
        "details": { "port": "24", "from": "up", "to": "down" }
      },
      {
        "sequence": 43,
        "id": "evt-43",
        "deviceId": "TEST-SW-01",
        "severity": "Recovery",
        "type": "uplink-recovered",
        "title": "Uplink recovered",
        "message": "Port 24 operational state changed: DOWN -> UP.",
        "state": "Recovered",
        "occurredUtc": "2026-07-20T08:31:10+00:00",
        "acknowledgedUtc": null,
        "recoveredUtc": "2026-07-20T08:31:10+00:00",
        "conditionKey": "uplink:24",
        "details": { "port": "24" }
      }
    ]
    """;

    private const string SnapshotV2Json = """
    {
      "agentId": "agent-poc-01",
      "connected": true,
      "ready": true,
      "readinessCode": "READY",
      "mockMode": false,
      "activeCritical": 1,
      "highWatermark": 77,
      "utc": "2026-07-20T08:31:00+00:00",
      "devices": [
        {
          "id": "TEST-SW-01", "displayName": "ACCESS-SW-01", "model": "IES4224GP", "uplinkPort": "24",
          "collection": [{
            "commandId": "interface_status", "capturedUtc": "2026-07-20T08:30:00+00:00",
            "data": { "uplinkOperationalUp": true, "portsUp": 24, "portsDown": 0 }
          }]
        }
      ]
    }
    """;

    [Fact]
    public void MapSnapshot_ComposesStatusAndDevicesContracts()
    {
        var result = AgentContractMapper.MapSnapshot(StatusJson, DevicesJson);

        Assert.Equal(AgentConnectionState.Connected, result.ConnectionState);
        Assert.Equal(42, result.LastEventSequence);
        Assert.Equal("Agent agent-poc-01", result.CollectorVersion);
        var device = Assert.Single(result.Devices);
        Assert.Equal("TEST-SW-01", device.Id);
        Assert.Equal("ACCESS-SW-01", device.Name);
        Assert.Equal("IES4224GP", device.Model);
        Assert.Equal(DeviceHealth.Critical, device.Health);
        Assert.Equal("2일 03:05", device.Uptime);
        Assert.Equal("장비 주소는 Agent에만 보관", device.AddressLabel);
        Assert.Contains(device.Metrics!, metric => metric.Label == "업링크" && metric.Value.Contains("DOWN", StringComparison.Ordinal));
        Assert.Contains(device.Metrics!, metric => metric.Label == "소프트웨어" && metric.Value == "1.2.3");
    }

    [Fact]
    public void MapEvents_MapsStructuredEventIdStateAndDeviceName()
    {
        var names = new Dictionary<string, string> { ["TEST-SW-01"] = "ACCESS-SW-01" };

        var result = AgentContractMapper.MapEvents(EventsJson, names);

        Assert.Equal(2, result.Count);
        Assert.Equal("evt-42", result[0].AgentEventId);
        Assert.Equal("ACCESS-SW-01", result[0].DeviceName);
        Assert.Equal(DeviceHealth.Critical, result[0].Severity);
        Assert.False(result[0].Acknowledged);
        Assert.False(result[0].Recovered);
        Assert.Equal("uplink:24", result[0].ConditionKey);
        Assert.Equal(DeviceHealth.Normal, result[1].Severity);
        Assert.True(result[1].Acknowledged);
        Assert.True(result[1].Recovered);
        Assert.Equal(DateTimeOffset.Parse("2026-07-20T08:31:10+00:00"), result[1].RecoveredAt);
    }

    [Fact]
    public void ApiRoutes_MatchAgentEndpointContractAndEscapeIds()
    {
        Assert.Equal("/api/v1/status", AgentApiRoutes.Status);
        Assert.Equal("/api/v1/devices", AgentApiRoutes.Devices);
        Assert.Equal("/api/v1/events?after=0", AgentApiRoutes.EventsAfter(-10));
        Assert.Equal("/api/v2/snapshot", AgentApiRoutes.SnapshotV2);
        Assert.Equal("/api/v2/events/recent?limit=500", AgentApiRoutes.RecentEventsV2(900));
        Assert.Equal("/api/v2/events/changes?after=0&limit=1", AgentApiRoutes.EventChangesV2(-5, 0));
        Assert.Equal("/api/v3/read-only-queries", AgentApiRoutes.ReadOnlyQueriesV3);
        Assert.Equal("/api/v1/commands/SW%2001/log_ram", AgentApiRoutes.Command("SW 01", "log_ram"));
        Assert.Equal("/api/v1/events/event%2F42/ack", AgentApiRoutes.Acknowledge("event/42"));
    }

    [Fact]
    public void MapSnapshotV3_NegotiatesReadOnlyQueryFeatureAndLimits()
    {
        const string json = """
        {
          "apiVersion":3,
          "agentId":"agent-poc-01",
          "mockMode":false,
          "utc":"2026-07-20T08:31:00Z",
          "counts":{"eventChangeHighWatermark":0},
          "channels":{
            "agent":{"status":"connected"},
            "api":{"status":"available"},
            "realtime":{"status":"available"},
            "readiness":{"status":"ready","code":"READY"}
          },
          "features":{
            "readOnlyQueries":{"enabled":true,"maxCommandLength":128,"maxOutputBytes":65536}
          },
          "devices":[]
        }
        """;

        var snapshot = AgentContractMapper.MapSnapshotV3(json);

        Assert.True(snapshot.ReadOnlyQueriesEnabled);
        Assert.Equal(128, snapshot.ReadOnlyQueryMaxCommandLength);
        Assert.Equal(65_536, snapshot.ReadOnlyQueryMaxOutputBytes);
    }

    [Fact]
    public void MapSnapshotV3_AbsentReadOnlyQueryFeatureDefaultsToDisabled()
    {
        const string json = """
        {
          "apiVersion":3,
          "agentId":"old-agent",
          "mockMode":false,
          "utc":"2026-07-20T08:31:00Z",
          "counts":{},
          "channels":{
            "agent":{"status":"connected"},
            "api":{"status":"available"},
            "realtime":{"status":"available"},
            "readiness":{"status":"ready","code":"READY"}
          },
          "devices":[]
        }
        """;

        var snapshot = AgentContractMapper.MapSnapshotV3(json);

        Assert.False(snapshot.ReadOnlyQueriesEnabled);
    }

    [Fact]
    public void MapReadOnlyQueryResult_MapsNormalizedOutputAndSessionMetadata()
    {
        const string json = """
        {
          "apiVersion":3,
          "deviceId":"ACCESS-SW-01",
          "command":"show port status",
          "startedUtc":"2026-07-20T08:31:00Z",
          "completedUtc":"2026-07-20T08:31:01Z",
          "elapsedMs":1234,
          "output":"Port 24 UP",
          "truncated":true,
          "sessionCount":1,
          "reconnectCount":0
        }
        """;

        var result = AgentContractMapper.MapReadOnlyQueryResult(json);

        Assert.Equal("ACCESS-SW-01", result.DeviceId);
        Assert.Equal("show port status", result.Command);
        Assert.Equal("Port 24 UP", result.Output);
        Assert.True(result.Truncated);
        Assert.Equal(1, result.SessionCount);
        Assert.Equal(0, result.ReconnectCount);
    }

    [Fact]
    public void MapSnapshotV2_MapsReadinessIdentityAndHighWatermark()
    {
        var snapshot = AgentContractMapper.MapSnapshotV2(SnapshotV2Json);

        Assert.Equal("agent-poc-01", snapshot.AgentId);
        Assert.True(snapshot.Ready);
        Assert.Equal("READY", snapshot.ReadinessCode);
        Assert.Equal(77, snapshot.HighWatermark);
        Assert.Equal(AgentConnectionState.Connected, snapshot.ConnectionState);
        var device = Assert.Single(snapshot.Devices);
        Assert.Equal("ACCESS-SW-01", device.Name);
        Assert.Equal(DeviceHealth.Normal, device.Health);
        Assert.Equal("등록된 모든 점검 정상", device.Summary);
        Assert.DoesNotContain(device.Metrics!, metric => metric.Label == "활성 장애");
        Assert.Contains("활성 장애 1건", snapshot.CollectorSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void MapEventChangePage_MapsAckAndRecoveryOfSameLogicalEvent()
    {
        const string json = """
        {
          "highWatermark": 12,
          "nextCursor": 12,
          "hasMore": false,
          "changes": [
            {"changeSequence":11,"changeKind":"Acknowledged","event":{"sequence":7,"id":"evt-7","deviceId":"sw","severity":"Critical","state":"Acknowledged","occurredUtc":"2026-07-20T08:30:00Z","acknowledgedUtc":"2026-07-20T08:31:00Z","isActiveCondition":true}},
            {"changeSequence":12,"changeKind":"Recovered","event":{"sequence":7,"id":"evt-7","deviceId":"sw","severity":"Critical","state":"Recovered","occurredUtc":"2026-07-20T08:30:00Z","recoveredUtc":"2026-07-20T08:32:00Z","isActiveCondition":false}}
          ]
        }
        """;

        var page = AgentContractMapper.MapEventChangePage(json);

        Assert.Equal(12, page.HighWatermark);
        Assert.False(page.HasMore);
        Assert.Equal([11L, 12L], page.Changes.Select(item => item.ChangeSequence));
        Assert.Equal("evt-7", page.Changes[0].Event.AgentEventId);
        Assert.True(page.Changes[0].Event.Acknowledged);
        Assert.True(page.Changes[1].Event.Recovered);
        Assert.False(page.Changes[1].Event.IsActiveCondition);
    }

    [Fact]
    public void MapEventChangePage_MapsRetentionResetContract()
    {
        const string json = """
        {"highWatermark":250,"nextCursor":250,"hasMore":false,"changes":[],"resetRequired":true,"resetCursor":250}
        """;

        var page = AgentContractMapper.MapEventChangePage(json);

        Assert.True(page.ResetRequired);
        Assert.Equal(250, page.ResetCursor);
        Assert.Empty(page.Changes);
    }

    [Fact]
    public void MapEvents_IgnoresMalformedItemsWithoutExposingRawPayload()
    {
        var result = AgentContractMapper.MapEvents("""[{"sequence":0},{"sequence":3,"id":"ok","deviceId":"sw","message":"safe"}]""");
        var item = Assert.Single(result);
        Assert.Equal("ok", item.AgentEventId);
        Assert.Equal("safe", item.Detail);
    }

    [Fact]
    public void MapSnapshot_CollectorHealthErrorUsesStableCodeWithoutEndpoint()
    {
        const string status = """
        { "agentId":"agent", "connected":true, "activeCritical":1, "utc":"2026-07-20T08:31:00+00:00" }
        """;
        const string devices = """
        [{
          "id":"SW-01", "displayName":"Switch", "model":"IES4224GP", "uplinkPort":"24",
          "collection":[{
            "commandId":"collector_health", "capturedUtc":"2026-07-20T08:30:00+00:00",
            "data":{"errorCode":"TCP_TIMEOUT", "lastAttemptUtc":"2026-07-20T08:30:00+00:00"}
          }]
        }]
        """;

        var device = Assert.Single(AgentContractMapper.MapSnapshot(status, devices).Devices);

        Assert.Equal(DeviceHealth.Disconnected, device.Health);
        Assert.Equal("스위치 수집 실패 · TCP_TIMEOUT", device.Summary);
        Assert.Contains(device.Metrics!, metric => metric.Label == "수집기" && metric.Value == "TCP_TIMEOUT");
        Assert.DoesNotContain("192.", device.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MapSnapshot_CollectorHealthDegradedIsWarningUntilFailureThreshold()
    {
        const string status = """
        { "agentId":"agent", "connected":true, "activeCritical":0, "utc":"2026-07-20T08:31:00+00:00" }
        """;
        const string devices = """
        [{
          "id":"SW-01", "displayName":"Switch", "model":"IES4224GP", "uplinkPort":"24",
          "collection":[
            {
              "commandId":"interface_status", "capturedUtc":"2026-07-20T08:30:00+00:00",
              "data":{"uplinkOperationalUp":true,"portsUp":24,"portsDown":0}
            },
            {
              "commandId":"collector_health", "capturedUtc":"2026-07-20T08:30:30+00:00",
              "data":{"state":"Degraded","errorCode":"TCP_TIMEOUT","consecutiveFailures":2}
            }
          ]
        }]
        """;

        var device = Assert.Single(AgentContractMapper.MapSnapshot(status, devices).Devices);

        Assert.Equal(DeviceHealth.Warning, device.Health);
        Assert.Contains("TCP_TIMEOUT", device.Summary, StringComparison.Ordinal);
        Assert.Contains(device.Metrics!, metric =>
            metric.Label == "수집기" && metric.Value == "TCP_TIMEOUT" && metric.Health == DeviceHealth.Warning);
    }

    [Fact]
    public void MapSnapshot_UnsupportedOptionalCollectorIsWarningNotDisconnected()
    {
        const string status = """
        { "agentId":"agent", "connected":true, "activeCritical":0, "utc":"2026-07-20T08:31:00+00:00" }
        """;
        const string devices = """
        [{
          "id":"SW-01", "displayName":"Switch", "model":"IES4224GP", "uplinkPort":"24",
          "collection":[
            {"commandId":"interface_status","capturedUtc":"2026-07-20T08:30:00+00:00","data":{"uplinkOperationalUp":true}},
            {"commandId":"collector_health","capturedUtc":"2026-07-20T08:30:30+00:00","data":{"state":"Unsupported","errorCode":"PARSER_UNSUPPORTED","commandId":"system"}}
          ]
        }]
        """;

        var device = Assert.Single(AgentContractMapper.MapSnapshot(status, devices).Devices);

        Assert.Equal(DeviceHealth.Warning, device.Health);
        Assert.Contains("PARSER_UNSUPPORTED", device.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MapSnapshot_EmptyPostChecksAreUnknownInsteadOfPass()
    {
        const string status = """
        { "agentId":"agent", "connected":true, "activeCritical":0, "utc":"2026-07-20T08:31:00+00:00" }
        """;
        const string devices = """
        [{
          "id":"SW-01", "displayName":"Switch", "model":"IES4224GP", "uplinkPort":"24",
          "collection":[
            {"commandId":"system","capturedUtc":"2026-07-20T08:30:00+00:00","data":{"postChecks":{}}},
            {"commandId":"interface_status","capturedUtc":"2026-07-20T08:30:00+00:00","data":{"uplinkOperationalUp":true}}
          ]
        }]
        """;

        var device = Assert.Single(AgentContractMapper.MapSnapshot(status, devices).Devices);

        Assert.Contains(device.Metrics!, metric => metric.Label == "POST" && metric.Value == "데이터 없음");
        Assert.DoesNotContain(device.Metrics!, metric => metric.Label == "POST" && metric.Value == "PASS");
    }

    [Fact]
    public void MapTelnetExecutionResultV4_PreservesSessionRetryCounts()
    {
        const string json = """
        {
          "apiVersion": 4,
          "requestId": "request-1",
          "success": true,
          "privilege": "privileged",
          "promptTerminator": "#",
          "startedUtc": "2026-07-23T01:00:00+00:00",
          "completedUtc": "2026-07-23T01:00:03+00:00",
          "durationMs": 3000,
          "sessionCount": 2,
          "reconnectCount": 1,
          "commands": [
            {
              "command": "show port status",
              "output": "1 Up",
              "truncated": false,
              "collectedUtc": "2026-07-23T01:00:03+00:00"
            }
          ]
        }
        """;

        var result = AgentContractMapper.MapTelnetExecutionResultV4(json);

        Assert.Equal(2, result.SessionCount);
        Assert.Equal(1, result.ReconnectCount);
    }

    [Fact]
    public void MapTelnetExecutionResultV4_ValidatesRequestAndNormalizedCommandMapping()
    {
        var json = TelnetResultJson(
            "request-1",
            true,
            [Command("show port status"), Command("show system")]);

        var result = AgentContractMapper.MapTelnetExecutionResultV4(
            json,
            "request-1",
            ["  show   port   status  ", "show system"],
            65_536);

        Assert.Equal(["show port status", "show system"], result.Commands.Select(item => item.Command));
    }

    [Fact]
    public void MapTelnetExecutionResultV4_RejectsFailureAndMismatchedRequestId()
    {
        var failed = TelnetResultJson(
            "request-1",
            false,
            [Command("show port status")]);
        var mismatched = TelnetResultJson(
            "different-request",
            true,
            [Command("show port status")]);

        Assert.Throws<JsonException>(() => AgentContractMapper.MapTelnetExecutionResultV4(
            failed,
            "request-1",
            ["show port status"],
            65_536));
        Assert.Throws<JsonException>(() => AgentContractMapper.MapTelnetExecutionResultV4(
            mismatched,
            "request-1",
            ["show port status"],
            65_536));
    }

    [Fact]
    public void MapTelnetExecutionResultV4_RejectsMissingExtraDuplicateAndReorderedOutputs()
    {
        var expected = new[] { "show port status", "show system" };
        var invalid = new[]
        {
            TelnetResultJson("request-1", true, [Command("show port status")]),
            TelnetResultJson(
                "request-1",
                true,
                [Command("show port status"), Command("show system"), Command("show version")]),
            TelnetResultJson(
                "request-1",
                true,
                [Command("show port status"), Command("show port status")]),
            TelnetResultJson(
                "request-1",
                true,
                [Command("show system"), Command("show port status")])
        };

        foreach (var json in invalid)
        {
            Assert.Throws<JsonException>(() => AgentContractMapper.MapTelnetExecutionResultV4(
                json,
                "request-1",
                expected,
                65_536));
        }
    }

    [Fact]
    public void MapTelnetExecutionResultV4_RejectsMissingFieldsAndOversizedCommandOutput()
    {
        var missingOutput = Command("show port status");
        missingOutput.Remove("output");
        var missingTruncated = Command("show port status");
        missingTruncated.Remove("truncated");
        var missingCollectedUtc = Command("show port status");
        missingCollectedUtc.Remove("collectedUtc");
        var invalid = new[]
        {
            TelnetResultJson("request-1", true, [missingOutput]),
            TelnetResultJson("request-1", true, [missingTruncated]),
            TelnetResultJson("request-1", true, [missingCollectedUtc]),
            TelnetResultJson(
                "request-1",
                true,
                [Command("show port status", new string('x', 65_537))]),
            TelnetResultJson("request-1", true, null, includeCommands: false)
        };

        foreach (var json in invalid)
        {
            Assert.Throws<JsonException>(() => AgentContractMapper.MapTelnetExecutionResultV4(
                json,
                "request-1",
                ["show port status"],
                65_536));
        }
    }

    private static Dictionary<string, object?> Command(string command, string output = "ok") => new()
    {
        ["command"] = command,
        ["output"] = output,
        ["truncated"] = false,
        ["collectedUtc"] = "2026-07-23T01:00:01Z"
    };

    private static string TelnetResultJson(
        string requestId,
        bool success,
        IReadOnlyList<Dictionary<string, object?>>? commands,
        bool includeCommands = true)
    {
        var result = new Dictionary<string, object?>
        {
            ["apiVersion"] = 4,
            ["requestId"] = requestId,
            ["success"] = success,
            ["privilege"] = "privileged",
            ["promptTerminator"] = "#",
            ["startedUtc"] = "2026-07-23T01:00:00Z",
            ["completedUtc"] = "2026-07-23T01:00:01Z",
            ["durationMs"] = 1000,
            ["sessionCount"] = 1,
            ["reconnectCount"] = 0
        };
        if (includeCommands)
        {
            result["commands"] = commands ?? [];
        }

        return JsonSerializer.Serialize(result);
    }
}
