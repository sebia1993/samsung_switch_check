using System.IO;
using System.Text;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerV0923CompatibilityTests
{
    [Fact]
    public void V0923Stores_LoadSanitizedFixtureWithoutQuarantineOrReset()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "SamsungSwitchWatch-V0923Compatibility",
            Guid.NewGuid().ToString("N"));
        var settingsPath = Path.Combine(folder, "viewer-settings.json");
        var devicesPath = Path.Combine(folder, "viewer-devices.json");
        var monitoringPath = Path.Combine(folder, "viewer-monitor-state.json");

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(settingsPath, V0923SettingsJson);
            File.WriteAllText(devicesPath, V0923DevicesJson);
            File.WriteAllText(monitoringPath, V0923MonitoringJson);

            var settingsStore = new ViewerSettingsStore(settingsPath);
            var settings = settingsStore.Load();
            var deviceStore = new ManagedDeviceStore(
                devicesPath,
                new SanitizedFixtureProtector());
            var deviceLoad = deviceStore.LoadWithStatus();
            var monitoringStore = new ViewerMonitoringStore(monitoringPath);

            Assert.Equal(ViewerSettingsLoadStatus.Ok, settingsStore.LastLoadStatus);
            Assert.Equal("https://agent.example.test:18443", settings.AgentUri);
            Assert.Equal(42, settings.LastEventSequence);
            Assert.Equal(42, settings.EventCursors["fixture-agent-cursor"]);
            Assert.True(settings.StartMinimizedToTray);

            Assert.Equal(ManagedDeviceLoadStatus.Ok, deviceLoad.Status);
            var device = Assert.Single(deviceLoad.Devices);
            Assert.Equal("fixture-device-01", device.Id);
            Assert.Equal("192.0.2.23", device.Host);
            Assert.True(device.ConnectionVerified);
            Assert.True(device.MonitoringEnabled);
            var credentials = deviceStore.GetSecrets(device.Id);
            Assert.Equal("fixture-operator", credentials.Username);
            Assert.Equal("fixture-passphrase", credentials.Password);
            Assert.Equal("fixture-enable", credentials.EnablePassword);

            Assert.Equal(ViewerMonitoringLoadStatus.Ok, monitoringStore.LastLoadStatus);
            var storedEvent = Assert.Single(monitoringStore.LoadEvents());
            Assert.Equal(42, storedEvent.Sequence);
            Assert.Equal("fixture-event-42", storedEvent.AgentEventId);
            Assert.Equal("fixture-device-01", storedEvent.DeviceId);

            Assert.Empty(Directory.GetFiles(folder, "*.corrupt-*"));
            Assert.True(File.Exists(settingsPath));
            Assert.Equal(V0923DevicesJson, File.ReadAllText(devicesPath));
            Assert.Equal(V0923MonitoringJson, File.ReadAllText(monitoringPath));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    // Frozen v0.9.23-poc store shapes with documentation-only, synthetic values.
    private const string V0923SettingsJson = """
    {
      "DemoMode": false,
      "AgentUri": "https://agent.example.test:18443",
      "AgentTrustPins": {
        "HTTPS://AGENT.EXAMPLE.TEST:18443": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
      },
      "LastEventSequence": 42,
      "EventCursors": {
        "fixture-agent-cursor": 42
      },
      "MiniTopmost": true,
      "MiniLeft": 120,
      "MiniTop": 80,
      "MainLeft": 160,
      "MainTop": 90,
      "MainWidth": 1440,
      "MainHeight": 900,
      "StartMinimizedToTray": true
    }
    """;

    private const string V0923DevicesJson = """
    {
      "SchemaVersion": 1,
      "Devices": [
        {
          "Id": "fixture-device-01",
          "DisplayName": "SANITIZED-SWITCH-01",
          "Model": "IES4224GP",
          "Host": "192.0.2.23",
          "Port": 23,
          "ProtectedUsername": "cHJvdGVjdGVkOmZpeHR1cmUtb3BlcmF0b3I=",
          "ProtectedPassword": "cHJvdGVjdGVkOmZpeHR1cmUtcGFzc3BocmFzZQ==",
          "ProtectedEnablePassword": "cHJvdGVjdGVkOmZpeHR1cmUtZW5hYmxl",
          "MonitoringEnabled": true,
          "ConnectionVerified": true,
          "LastConnectionTestUtc": "2026-07-20T01:02:03+00:00",
          "LastConnectionTestCode": "OK",
          "UpdatedUtc": "2026-07-20T01:02:04+00:00"
        }
      ]
    }
    """;

    private const string V0923MonitoringJson = """
    {
      "SchemaVersion": 3,
      "NextSequence": 42,
      "LastStartedUtc": "2026-07-20T01:00:00+00:00",
      "LastHeartbeatUtc": "2026-07-20T01:02:00+00:00",
      "LastStoppedUtc": "2026-07-20T01:02:01+00:00",
      "Baselines": {},
      "ActiveFailures": {},
      "ActiveInterfaceConditions": {},
      "Capabilities": {},
      "Events": [
        {
          "Sequence": 42,
          "AgentEventId": "fixture-event-42",
          "DeviceId": "fixture-device-01",
          "DeviceName": "SANITIZED-SWITCH-01",
          "OccurredAt": "2026-07-20T01:01:00+00:00",
          "Severity": 1,
          "Kind": "fixture",
          "Title": "Sanitized compatibility event",
          "Detail": "Synthetic state only"
        }
      ]
    }
    """;

    private sealed class SanitizedFixtureProtector : IViewerSecretProtector
    {
        public string Protect(string plainText) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"protected:{plainText}"));

        public string Unprotect(string protectedText)
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(protectedText));
            const string prefix = "protected:";
            if (!decoded.StartsWith(prefix, StringComparison.Ordinal))
            {
                throw new InvalidDataException("SANITIZED_FIXTURE_INVALID");
            }

            return decoded[prefix.Length..];
        }
    }
}
