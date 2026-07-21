using System.IO;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerSettingsTests
{
    [Fact]
    public void Sanitize_MigratesLegacyHttpsAndRemovesUnsafeUriParts()
    {
        var source = new ViewerSettings
        {
            DemoMode = false,
            AgentUri = "https://user:secret@monitor.example.test:18443/path?value=leak#part",
            LastEventSequence = -50,
            MainWidth = 30,
            MainHeight = double.PositiveInfinity
        };

        var clean = ViewerSettingsSanitizer.Sanitize(source);

        Assert.Equal("http://monitor.example.test:18443", clean.AgentUri);
        Assert.Equal(0, clean.LastEventSequence);
        Assert.Equal(1280, clean.MainWidth);
        Assert.Equal(900, clean.MainHeight);
        Assert.DoesNotContain("secret", clean.AgentUri, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://monitor.example.test:18443", "http://monitor.example.test:18443")]
    [InlineData("https://192.0.2.10:18443", "http://192.0.2.10:18443")]
    [InlineData("https://monitor.example.test", "http://monitor.example.test:443")]
    public void Sanitize_AcceptsHttpAndMigratesHttps(string input, string expected)
    {
        var clean = ViewerSettingsSanitizer.Sanitize(new ViewerSettings { AgentUri = input });

        Assert.Equal(expected, clean.AgentUri);
        Assert.True(ViewerSettingsSanitizer.IsValidForLiveConnection(clean, out var reason));
        Assert.Empty(reason);
    }

    [Theory]
    [InlineData("file:///c:/agent")]
    [InlineData("not a uri")]
    [InlineData("http://monitor.example.test:0")]
    [InlineData("http://[::1]:18443")]
    public void Sanitize_RejectsUnsupportedAgentUris(string input)
    {
        var clean = ViewerSettingsSanitizer.Sanitize(new ViewerSettings { AgentUri = input });

        Assert.Empty(clean.AgentUri);
        Assert.False(ViewerSettingsSanitizer.IsValidForLiveConnection(clean, out _));
    }

    [Theory]
    [InlineData("10.10.10.20", "18443", "http://10.10.10.20:18443")]
    [InlineData("monitor-pc.corp.local", "18443", "http://monitor-pc.corp.local:18443")]
    [InlineData("localhost", "80", "http://localhost")]
    public void ConnectionInput_AcceptsIpv4OrDnsAndPort(string address, string port, string expected)
    {
        Assert.True(ViewerSettingsSanitizer.TryBuildAgentUri(address, port, out var uri, out var reason));
        Assert.Equal(expected, uri);
        Assert.Empty(reason);
    }

    [Theory]
    [InlineData("http://monitor", "18443")]
    [InlineData("::1", "18443")]
    [InlineData("monitor pc", "18443")]
    [InlineData("monitor", "0")]
    [InlineData("monitor", "65536")]
    [InlineData("monitor", "not-a-port")]
    public void ConnectionInput_RejectsUnsupportedValues(string address, string port) =>
        Assert.False(ViewerSettingsSanitizer.TryBuildAgentUri(address, port, out _, out _));

    [Fact]
    public void EventCursor_IsScopedByAgentUriAndAgentId()
    {
        var settings = new ViewerSettings { AgentUri = "http://agent-a.example.test:18443" };
        settings.SetEventCursor("agent-1", 42);

        Assert.True(settings.TryGetEventCursor("agent-1", out var cursor));
        Assert.Equal(42, cursor);
        Assert.False(settings.TryGetEventCursor("agent-2", out _));

        settings.AgentUri = "http://agent-b.example.test:18443";
        Assert.False(settings.TryGetEventCursor("agent-1", out _));
    }

    [Fact]
    public void Store_MigratesLegacyConnectionDropsSecurityFieldsAndPreservesViewerState()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-SettingsTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "viewer-settings.json");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, """
            {
              "DemoMode": false,
              "AgentUri": "https://agent.example.test:18443",
              "CertificateFingerprint": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
              "CertificateFingerprints": ["BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"],
              "ProtectedBearerToken": "dpapi:not-base64",
              "EventCursors": {"LEGACY-PIN-IDENTITY": 77},
              "MiniTopmost": false,
              "MainWidth": 1600,
              "MainHeight": 920,
              "StartMinimizedToTray": true
            }
            """);
            var store = new ViewerSettingsStore(path);

            var loaded = store.Load();

            Assert.Equal(ViewerSettingsLoadStatus.Ok, store.LastLoadStatus);
            Assert.False(loaded.DemoMode);
            Assert.Equal("http://agent.example.test:18443", loaded.AgentUri);
            Assert.False(loaded.MiniTopmost);
            Assert.Equal(1600, loaded.MainWidth);
            Assert.Equal(920, loaded.MainHeight);
            Assert.True(loaded.StartMinimizedToTray);
            Assert.Equal(77, loaded.EventCursors["LEGACY-PIN-IDENTITY"]);

            var migratedJson = File.ReadAllText(path);
            Assert.DoesNotContain("https://", migratedJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Certificate", migratedJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Fingerprint", migratedJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Bearer", migratedJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Token", migratedJson, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("LEGACY-PIN-IDENTITY", migratedJson, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Store_CorruptJsonIsQuarantinedAndReturnsSafeDefaults()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-SettingsTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "viewer-settings.json");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, "{ definitely-not-json");
            var store = new ViewerSettingsStore(path);

            var loaded = store.Load();

            Assert.True(loaded.DemoMode);
            Assert.Equal(ViewerSettingsLoadStatus.Corrupt, store.LastLoadStatus);
            Assert.False(File.Exists(path));
            Assert.Single(Directory.GetFiles(folder, "viewer-settings.json.corrupt-*"));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void StartupWindowPolicy_HonorsTrayStartUnlessConnectionNeedsAttention()
    {
        var settings = new ViewerSettings { StartMinimizedToTray = true };

        Assert.False(StartupWindowPolicy.ShouldShowMainWindow(settings, needsConnection: false));
        Assert.True(StartupWindowPolicy.ShouldShowMainWindow(settings, needsConnection: true));

        settings.StartMinimizedToTray = false;
        Assert.True(StartupWindowPolicy.ShouldShowMainWindow(settings, needsConnection: false));
    }

    [Fact]
    public void Sanitize_PreservesStartMinimizedToTray()
    {
        var clean = ViewerSettingsSanitizer.Sanitize(new ViewerSettings { StartMinimizedToTray = true });
        Assert.True(clean.StartMinimizedToTray);
    }
}
