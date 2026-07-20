using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SamsungSwitchWatch.Viewer.Services;

public sealed class ViewerSettings
{
    public bool DemoMode { get; set; } = true;
    public string AgentUri { get; set; } = "https://localhost:18443";
    public string CertificateFingerprint { get; set; } = string.Empty;
    public string ProtectedBearerToken { get; set; } = string.Empty;
    [JsonIgnore] public string BearerToken { get; set; } = string.Empty;
    public long LastEventSequence { get; set; }
    public bool MiniTopmost { get; set; } = true;
    public double MiniLeft { get; set; } = double.NaN;
    public double MiniTop { get; set; } = double.NaN;
    public double MainLeft { get; set; } = double.NaN;
    public double MainTop { get; set; } = double.NaN;
    public double MainWidth { get; set; } = 1440;
    public double MainHeight { get; set; } = 900;
    public bool StartMinimizedToTray { get; set; }
}

public static class ViewerSettingsSanitizer
{
    public static ViewerSettings Sanitize(ViewerSettings? input)
    {
        input ??= new ViewerSettings();
        var uri = NormalizeAgentUri(input.AgentUri);
        return new ViewerSettings
        {
            DemoMode = input.DemoMode,
            AgentUri = uri,
            CertificateFingerprint = NormalizeFingerprint(input.CertificateFingerprint),
            ProtectedBearerToken = Limit(input.ProtectedBearerToken, 8192),
            BearerToken = Limit(input.BearerToken, 4096),
            LastEventSequence = Math.Max(0, input.LastEventSequence),
            MiniTopmost = input.MiniTopmost,
            MiniLeft = NormalizeCoordinate(input.MiniLeft),
            MiniTop = NormalizeCoordinate(input.MiniTop),
            MainLeft = NormalizeCoordinate(input.MainLeft),
            MainTop = NormalizeCoordinate(input.MainTop),
            MainWidth = Math.Clamp(IsFinite(input.MainWidth) ? input.MainWidth : 1440, 1280, 7680),
            MainHeight = Math.Clamp(IsFinite(input.MainHeight) ? input.MainHeight : 900, 720, 4320),
            StartMinimizedToTray = input.StartMinimizedToTray
        };
    }

    public static string NormalizeFingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var hex = new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();
        return hex.Length == 64 ? hex : string.Empty;
    }

    public static bool IsValidForLiveConnection(ViewerSettings settings, out string reason)
    {
        var clean = Sanitize(settings);
        if (!Uri.TryCreate(clean.AgentUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            reason = "Agent 주소는 HTTPS URL이어야 합니다.";
            return false;
        }
        if (clean.CertificateFingerprint.Length != 64)
        {
            reason = "인증서 SHA-256 지문은 64자리여야 합니다.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(clean.BearerToken))
        {
            reason = "페어링 토큰을 입력해야 합니다.";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static string NormalizeAgentUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return "https://localhost:18443";
        }
        var builder = new UriBuilder(uri)
        {
            Path = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };
        return builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static string Limit(string? value, int maxLength) =>
        string.IsNullOrEmpty(value) ? string.Empty : value[..Math.Min(value.Length, maxLength)];

    private static double NormalizeCoordinate(double value) => IsFinite(value) ? Math.Clamp(value, -32000, 32000) : -32000;
    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
}

public sealed class ViewerSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public ViewerSettingsStore(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SamsungSwitchWatch",
            "viewer-settings.json");
    }

    public string SettingsPath => _settingsPath;

    public ViewerSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return new ViewerSettings();
            var settings = JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllText(_settingsPath), JsonOptions);
            settings = ViewerSettingsSanitizer.Sanitize(settings);
            settings.BearerToken = Unprotect(settings.ProtectedBearerToken);
            return settings;
        }
        catch
        {
            return new ViewerSettings();
        }
    }

    public void Save(ViewerSettings settings)
    {
        var clean = ViewerSettingsSanitizer.Sanitize(settings);
        clean.ProtectedBearerToken = Protect(clean.BearerToken);
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporaryPath = _settingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(clean, JsonOptions), new UTF8Encoding(false));
        File.Move(temporaryPath, _settingsPath, true);
    }

    private static string Protect(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        try
        {
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(bytes);
        }
        catch
        {
            // A token must never fall back to clear text. Live mode will ask for pairing again.
            return string.Empty;
        }
    }

    private static string Unprotect(string value)
    {
        if (!value.StartsWith("dpapi:", StringComparison.Ordinal)) return string.Empty;
        try
        {
            var bytes = Convert.FromBase64String(value[6..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
        }
        catch { return string.Empty; }
    }
}
