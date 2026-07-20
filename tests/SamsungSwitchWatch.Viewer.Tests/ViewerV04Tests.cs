using System.Net;
using System.IO;
using System.Text;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;
using SamsungSwitchWatch.Viewer.ViewModels;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerV04Tests
{
    [Fact]
    public void CertificatePins_AcceptCurrentAndPlannedPinsWithoutTrustingThirdValue()
    {
        var current = new string('A', 64);
        var planned = new string('B', 64);

        Assert.True(ViewerSettingsSanitizer.TryParseFingerprintInput(
            $"{current}\r\n{planned},{current.ToLowerInvariant()}", out var pins, out var reason));
        Assert.Empty(reason);
        Assert.Equal([current, planned], pins);

        var clean = ViewerSettingsSanitizer.Sanitize(new ViewerSettings
        {
            CertificateFingerprint = current,
            CertificateFingerprints = [planned, current]
        });
        Assert.Equal(current, clean.CertificateFingerprint);
        Assert.Equal([current, planned], clean.AcceptedCertificateFingerprints);

        Assert.False(ViewerSettingsSanitizer.TryParseFingerprintInput(
            $"{current},{planned},{new string('C', 64)}", out _, out reason));
        Assert.Contains("최대 2개", reason, StringComparison.Ordinal);

        var actual = Convert.FromHexString(planned);
        Assert.True(CertificatePinMatcher.Matches(actual, clean.AcceptedCertificateFingerprints
            .Select(Convert.FromHexString).ToArray()));
        Assert.False(CertificatePinMatcher.Matches(Convert.FromHexString(new string('D', 64)),
            clean.AcceptedCertificateFingerprints.Select(Convert.FromHexString).ToArray()));
    }

    [Fact]
    public void MapSnapshotV3_UsesAuthoritativeCountsAndSeparatesOperationalChannels()
    {
        const string json = """
        {
          "apiVersion":3,
          "agentId":"agent-v3",
          "mockMode":false,
          "utc":"2026-07-21T03:00:00Z",
          "counts":{"configuredDevices":3,"activeCritical":1,"unacknowledged":1201,"lastSequence":44,"eventChangeHighWatermark":55},
          "channels":{
            "agent":{"status":"connected"},
            "api":{"status":"available","version":3},
            "realtime":{"status":"available","meaning":"agent-endpoint-available"},
            "readiness":{"status":"not-ready","code":"STORAGE_WRITE_FAILED","schemaVersion":3},
            "storage":{"ready":false,"errorCode":"STORAGE_WRITE_FAILED","schemaVersion":3},
            "certificate":{"state":"expiring","notAfterUtc":"2026-08-01T00:00:00Z","daysRemaining":11}
          },
          "devices":[{
            "id":"SW-02","displayName":"ACCESS-SW-02","model":"IES4028XP","uplinkPort":"25",
            "lastCollectionUtc":"2026-07-21T02:59:30Z",
            "collectionHealth":{"state":"Degraded","errorCode":"COMMAND_TIMEOUT"},
            "capabilities":[
              {"commandId":"version","supported":true,"state":"Healthy"},
              {"commandId":"log_ram","supported":false,"state":"Unsupported","errorCode":"PARSER_UNSUPPORTED"}
            ],
            "collections":[{"commandId":"interface_status","capturedUtc":"2026-07-21T02:59:30Z","data":{"uplinkOperationalUp":true,"portsUp":24,"portsDown":0}}]
          }]
        }
        """;

        var snapshot = AgentContractMapper.MapSnapshotV3(json);

        Assert.Equal(3, snapshot.ApiVersion);
        Assert.Equal(1201, snapshot.AuthoritativeUnacknowledged);
        Assert.Equal(55, snapshot.HighWatermark);
        Assert.Equal(AgentConnectionState.Stale, snapshot.ConnectionState);
        var device = Assert.Single(snapshot.Devices);
        Assert.Equal("IES4028XP", device.Model);
        Assert.Equal(2, device.Capabilities?.Count);
        Assert.Equal(DeviceHealth.Warning, device.Health);
        Assert.Contains(snapshot.OperationalStatuses!, item => item.Code == "DB_INTEGRITY_FAILED");
        Assert.Contains(snapshot.OperationalStatuses!, item => item.Code == "CERT_EXPIRING");
    }

    [Fact]
    public async Task DashboardFilters_SearchDisplayedSubsetButKeepsAgentAuthoritativeTotal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"viewer-v04-{Guid.NewGuid():N}.json");
        var viewModel = new DashboardViewModel(new ViewerSettings(), new ViewerSettingsStore(path));
        var now = DateTimeOffset.UtcNow;
        try
        {
            viewModel.ApplySnapshot(new AgentSnapshotDto(now, AgentConnectionState.Connected, [], 2,
                "Agent", "정상", AuthoritativeUnacknowledged: 1201, ApiVersion: 3));
            viewModel.ApplyEvents([
                Event(1, "새 로그", DeviceHealth.Warning, "STP Root 변경", "로그 메시지"),
                Event(2, "복구", DeviceHealth.Normal, "업링크 복구", "DOWN → UP") with
                {
                    Recovered = true,
                    Acknowledged = true
                }
            ], false);

            Assert.Equal(1201, viewModel.UnacknowledgedCount);
            Assert.Equal(2, viewModel.VisibleEventCount);

            viewModel.SelectedEventFilter = viewModel.EventFilters.Single(item => item.Value == EventFilter.NewLog);
            Assert.Single(viewModel.FilteredEvents);
            Assert.Equal("STP Root 변경", viewModel.FilteredEvents[0].Title);

            viewModel.EventSearchText = "없는 문자열";
            Assert.Empty(viewModel.FilteredEvents);
            Assert.Equal(1201, viewModel.UnacknowledgedCount);
        }
        finally
        {
            await viewModel.DisposeAsync();
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task Export_WritesUtf8BomCsvAndAtomicSanitizedJsonWithoutIdentifiersOrRawOutput()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-V04", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var item = new EventViewModel(Event(7, "새 로그", DeviceHealth.Critical,
                "host=secret-sw.corp 장애", "user=operator 192.0.2.44 AA:BB:CC:DD:EE:FF token=secret"));
            var service = new ViewerExportService();
            var csvPath = Path.Combine(folder, "events.csv");
            var jsonPath = Path.Combine(folder, "events.json");

            Assert.True((await service.ExportAsync(csvPath, ViewerExportFormat.Csv, [item])).Success);
            Assert.True((await service.ExportAsync(jsonPath, ViewerExportFormat.Json, [item])).Success);

            var csvBytes = await File.ReadAllBytesAsync(csvPath);
            Assert.Equal([0xEF, 0xBB, 0xBF], csvBytes.Take(3));
            var csv = Encoding.UTF8.GetString(csvBytes);
            var json = await File.ReadAllTextAsync(jsonPath);
            foreach (var output in new[] { csv, json })
            {
                Assert.DoesNotContain("SW-REAL-01", output, StringComparison.Ordinal);
                Assert.DoesNotContain("192.0.2.44", output, StringComparison.Ordinal);
                Assert.DoesNotContain("AA:BB:CC:DD:EE:FF", output, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("operator", output, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("secret-sw.corp", output, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("DEVICE-", output, StringComparison.Ordinal);
            }
            Assert.Contains("\"rawOutputIncluded\": false", json, StringComparison.Ordinal);
            Assert.DoesNotContain(Directory.EnumerateFiles(folder), file => file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void NativeToastBoundary_UsesWindowsBackendAndActivationCallback()
    {
        using var backend = new FakeToastBackend();
        EventViewModel? opened = null;
        using var service = new AlertPopupService(item => opened = item, backend);
        var expected = new EventViewModel(Event(9, "상태 변경", DeviceHealth.Critical, "업링크 Down", "UP → DOWN"));

        service.Enqueue(expected);

        Assert.True(backend.WaitUntilShown(TimeSpan.FromSeconds(5)), "Native toast was not dispatched in time.");
        Assert.Same(expected, backend.Item);
        backend.Activate();
        Assert.Same(expected, opened);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    public void ApiCompatibility_FallsBackOnlyWhenV3RouteIsMissing(HttpStatusCode status, bool expected) =>
        Assert.Equal(expected, ApiCompatibilityPolicy.ShouldFallback(status));

    private static SwitchEventDto Event(long sequence, string kind, DeviceHealth health, string title, string detail) =>
        new(sequence, $"event-{sequence}", "SW-REAL-01", "ACCESS-SW-REAL-01", DateTimeOffset.UtcNow,
            health, kind, title, detail);

    private sealed class FakeToastBackend : IWindowsToastBackend, IDisposable
    {
        private readonly ManualResetEventSlim _shown = new(false);
        private Action<EventViewModel>? _activated;
        public EventViewModel? Item { get; private set; }

        public bool TryShow(EventViewModel item, Action<EventViewModel> activated)
        {
            _activated = activated;
            Item = item;
            _shown.Set();
            return true;
        }

        public bool WaitUntilShown(TimeSpan timeout) => _shown.Wait(timeout);

        public void Activate()
        {
            if (Item is not null) _activated?.Invoke(Item);
        }

        public void Dispose() => _shown.Dispose();
    }
}
