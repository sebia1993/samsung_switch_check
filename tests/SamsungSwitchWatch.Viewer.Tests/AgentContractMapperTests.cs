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
    }

    [Fact]
    public void ApiRoutes_MatchAgentEndpointContractAndEscapeIds()
    {
        Assert.Equal("/api/v1/status", AgentApiRoutes.Status);
        Assert.Equal("/api/v1/devices", AgentApiRoutes.Devices);
        Assert.Equal("/api/v1/events?after=0", AgentApiRoutes.EventsAfter(-10));
        Assert.Equal("/api/v1/commands/SW%2001/log_ram", AgentApiRoutes.Command("SW 01", "log_ram"));
        Assert.Equal("/api/v1/events/event%2F42/ack", AgentApiRoutes.Acknowledge("event/42"));
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
}
