using SamsungSwitchWatch.Viewer.Services;
using System.IO;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerSettingsTests
{
    [Fact]
    public void Sanitize_RemovesUnsafeUriPartsAndNormalizesFingerprint()
    {
        var fingerprint = string.Join(":", Enumerable.Repeat("ab", 32));
        var source = new ViewerSettings
        {
            DemoMode = false,
            AgentUri = "https://user:secret@monitor.example.test:18443/path?token=leak#part",
            CertificateFingerprint = fingerprint,
            LastEventSequence = -50,
            MainWidth = 30,
            MainHeight = double.PositiveInfinity
        };

        var clean = ViewerSettingsSanitizer.Sanitize(source);

        Assert.Equal("https://monitor.example.test:18443", clean.AgentUri);
        Assert.Equal(string.Concat(Enumerable.Repeat("AB", 32)), clean.CertificateFingerprint);
        Assert.Equal(0, clean.LastEventSequence);
        Assert.Equal(1280, clean.MainWidth);
        Assert.Equal(900, clean.MainHeight);
        Assert.DoesNotContain("secret", clean.AgentUri, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("http://monitor.example.test:18443")]
    [InlineData("file:///c:/agent")]
    [InlineData("not a uri")]
    public void Sanitize_RejectsNonHttpsAgentUris(string input)
    {
        var clean = ViewerSettingsSanitizer.Sanitize(new ViewerSettings { AgentUri = input });
        Assert.Empty(clean.AgentUri);
        clean.BearerToken = "token";
        clean.CertificateFingerprint = new string('A', 64);
        Assert.False(ViewerSettingsSanitizer.IsValidForLiveConnection(clean, out _));
    }

    [Fact]
    public void LiveValidation_RequiresPinnedCertificateAndToken()
    {
        var settings = new ViewerSettings
        {
            DemoMode = false,
            AgentUri = "https://monitor.example.test:18443",
            CertificateFingerprint = new string('A', 64),
            BearerToken = "paired-token"
        };

        Assert.True(ViewerSettingsSanitizer.IsValidForLiveConnection(settings, out var reason));
        Assert.Empty(reason);

        settings.BearerToken = string.Empty;
        Assert.False(ViewerSettingsSanitizer.IsValidForLiveConnection(settings, out reason));
        Assert.Contains("토큰", reason);
    }

    [Fact]
    public void Store_DoesNotPersistTokenAsPlainText()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-SettingsTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "viewer-settings.json");
        try
        {
            var store = new ViewerSettingsStore(path);
            store.Save(new ViewerSettings { BearerToken = "sensitive-pair-token" });

            var json = File.ReadAllText(path);
            Assert.DoesNotContain("sensitive-pair-token", json, StringComparison.Ordinal);
            Assert.Equal("sensitive-pair-token", store.Load().BearerToken);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void EventCursor_IsScopedByAgentUriFingerprintAndAgentId()
    {
        var settings = new ViewerSettings
        {
            AgentUri = "https://agent-a.example.test:18443",
            CertificateFingerprint = new string('A', 64)
        };
        settings.SetEventCursor("agent-1", 42);

        Assert.True(settings.TryGetEventCursor("agent-1", out var cursor));
        Assert.Equal(42, cursor);
        Assert.False(settings.TryGetEventCursor("agent-2", out _));

        settings.AgentUri = "https://agent-b.example.test:18443";
        Assert.False(settings.TryGetEventCursor("agent-1", out _));
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
    public void Store_InvalidProtectedTokenRequiresPairingWithoutDestroyingConnectionSettings()
    {
        var folder = Path.Combine(Path.GetTempPath(), "SamsungSwitchWatch-SettingsTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "viewer-settings.json");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, """
            {"DemoMode":false,"AgentUri":"https://agent.example.test:18443","CertificateFingerprint":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","ProtectedBearerToken":"dpapi:not-base64"}
            """);
            var store = new ViewerSettingsStore(path);

            var loaded = store.Load();

            Assert.Equal(ViewerSettingsLoadStatus.NeedsPairing, store.LastLoadStatus);
            Assert.False(loaded.DemoMode);
            Assert.Equal("https://agent.example.test:18443", loaded.AgentUri);
            Assert.Empty(loaded.BearerToken);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void StartupWindowPolicy_HonorsTrayStartUnlessPairingNeedsAttention()
    {
        var settings = new ViewerSettings { StartMinimizedToTray = true };

        Assert.False(StartupWindowPolicy.ShouldShowMainWindow(settings, needsPairing: false));
        Assert.True(StartupWindowPolicy.ShouldShowMainWindow(settings, needsPairing: true));

        settings.StartMinimizedToTray = false;
        Assert.True(StartupWindowPolicy.ShouldShowMainWindow(settings, needsPairing: false));
    }

    [Fact]
    public void Sanitize_PreservesStartMinimizedToTray()
    {
        var clean = ViewerSettingsSanitizer.Sanitize(new ViewerSettings { StartMinimizedToTray = true });
        Assert.True(clean.StartMinimizedToTray);
    }
}
