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
        Assert.Equal("https://localhost:18443", clean.AgentUri);
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
}
