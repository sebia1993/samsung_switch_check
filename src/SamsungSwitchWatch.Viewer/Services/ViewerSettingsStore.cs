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
    public List<string> CertificateFingerprints { get; set; } = [];
    public string ProtectedBearerToken { get; set; } = string.Empty;
    [JsonIgnore] public string BearerToken { get; set; } = string.Empty;
    public long LastEventSequence { get; set; }
    public Dictionary<string, long> EventCursors { get; set; } = new(StringComparer.Ordinal);
    public bool MiniTopmost { get; set; } = true;
    public double MiniLeft { get; set; } = double.NaN;
    public double MiniTop { get; set; } = double.NaN;
    public double MainLeft { get; set; } = double.NaN;
    public double MainTop { get; set; } = double.NaN;
    public double MainWidth { get; set; } = 1440;
    public double MainHeight { get; set; } = 900;
    public bool StartMinimizedToTray { get; set; }

    [JsonIgnore]
    public IReadOnlyList<string> AcceptedCertificateFingerprints => ViewerSettingsSanitizer
        .NormalizeFingerprints((CertificateFingerprints ?? []).Prepend(CertificateFingerprint));

    public string BuildAgentIdentity(string agentId)
    {
        var pins = string.Join(',', AcceptedCertificateFingerprints.Order(StringComparer.Ordinal));
        var material = $"{agentId.Trim()}\n{AgentUri.Trim().ToUpperInvariant()}\n{pins}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    public bool TryGetEventCursor(string agentId, out long cursor) =>
        EventCursors.TryGetValue(BuildAgentIdentity(agentId), out cursor);

    public void SetEventCursor(string agentId, long cursor)
    {
        var identity = BuildAgentIdentity(agentId);
        if (!EventCursors.ContainsKey(identity) && EventCursors.Count >= 32)
        {
            EventCursors.Remove(EventCursors.Keys.First());
        }
        EventCursors[identity] = Math.Max(0, cursor);
        LastEventSequence = Math.Max(0, cursor);
    }
}

public static class ViewerSettingsSanitizer
{
    public static ViewerSettings Sanitize(ViewerSettings? input)
    {
        input ??= new ViewerSettings();
        var uri = NormalizeAgentUri(input.AgentUri);
        var fingerprints = NormalizeFingerprints((input.CertificateFingerprints ?? []).Prepend(input.CertificateFingerprint));
        return new ViewerSettings
        {
            DemoMode = input.DemoMode,
            AgentUri = uri,
            CertificateFingerprint = fingerprints.FirstOrDefault() ?? string.Empty,
            CertificateFingerprints = fingerprints.ToList(),
            ProtectedBearerToken = Limit(input.ProtectedBearerToken, 8192),
            BearerToken = Limit(input.BearerToken, 4096),
            LastEventSequence = Math.Max(0, input.LastEventSequence),
            EventCursors = (input.EventCursors ?? new Dictionary<string, long>())
                .Where(item => item.Key.Length is > 0 and <= 128)
                .Take(32)
                .ToDictionary(item => item.Key, item => Math.Max(0, item.Value), StringComparer.Ordinal),
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

    public static IReadOnlyList<string> NormalizeFingerprints(IEnumerable<string?>? values)
    {
        if (values is null) return [];
        return values
            .SelectMany(SplitFingerprintInput)
            .Select(NormalizeFingerprint)
            .Where(value => value.Length == 64)
            .Distinct(StringComparer.Ordinal)
            .Take(2)
            .ToArray();
    }

    public static bool TryParseFingerprintInput(
        string? value,
        out IReadOnlyList<string> fingerprints,
        out string reason)
    {
        var parts = SplitFingerprintInput(value).ToArray();
        if (parts.Length == 0)
        {
            fingerprints = [];
            reason = "인증서 SHA-256 지문을 하나 이상 입력해야 합니다.";
            return false;
        }

        var invalid = parts.Any(part => NormalizeFingerprint(part).Length != 64);
        var normalized = parts
            .Select(NormalizeFingerprint)
            .Where(item => item.Length == 64)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (invalid)
        {
            fingerprints = [];
            reason = "각 인증서 SHA-256 지문은 64자리 16진수여야 합니다.";
            return false;
        }
        if (normalized.Length > 2)
        {
            fingerprints = [];
            reason = "인증서 지문은 현재 인증서와 예정 인증서를 합쳐 최대 2개까지 입력할 수 있습니다.";
            return false;
        }

        fingerprints = normalized;
        reason = string.Empty;
        return true;
    }

    public static bool IsValidForLiveConnection(ViewerSettings settings, out string reason)
    {
        var clean = Sanitize(settings);
        if (!Uri.TryCreate(clean.AgentUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            reason = "Agent 주소는 HTTPS URL이어야 합니다.";
            return false;
        }
        if (clean.AcceptedCertificateFingerprints.Count is < 1 or > 2)
        {
            reason = "인증서 SHA-256 지문은 64자리이며 최대 2개까지 허용됩니다.";
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
            return string.Empty;
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

    private static IEnumerable<string> SplitFingerprintInput(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value.Split([',', ';', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

public sealed class ViewerSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public ViewerSettingsLoadStatus LastLoadStatus { get; private set; } = ViewerSettingsLoadStatus.Ok;

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
            if (!File.Exists(_settingsPath))
            {
                LastLoadStatus = ViewerSettingsLoadStatus.Missing;
                return new ViewerSettings();
            }
            var settings = JsonSerializer.Deserialize<ViewerSettings>(File.ReadAllText(_settingsPath), JsonOptions);
            settings = ViewerSettingsSanitizer.Sanitize(settings);
            if (!TryUnprotect(settings.ProtectedBearerToken, out var token))
            {
                LastLoadStatus = ViewerSettingsLoadStatus.NeedsPairing;
                settings.BearerToken = string.Empty;
                return settings;
            }
            settings.BearerToken = token;
            LastLoadStatus = ViewerSettingsLoadStatus.Ok;
            return settings;
        }
        catch
        {
            QuarantineCorruptSettings();
            LastLoadStatus = ViewerSettingsLoadStatus.Corrupt;
            return new ViewerSettings();
        }
    }

    public void Save(ViewerSettings settings)
    {
        var clean = ViewerSettingsSanitizer.Sanitize(settings);
        if (!string.IsNullOrEmpty(clean.BearerToken))
        {
            clean.ProtectedBearerToken = Protect(clean.BearerToken);
        }
        else
        {
            clean.ProtectedBearerToken = settings.ProtectedBearerToken;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        var temporaryPath = _settingsPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(clean, JsonOptions), new UTF8Encoding(false));
            File.Move(temporaryPath, _settingsPath, true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
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
            throw new InvalidOperationException("VIEWER_TOKEN_PROTECT_FAILED");
        }
    }

    private static bool TryUnprotect(string value, out string token)
    {
        token = string.Empty;
        if (string.IsNullOrEmpty(value)) return true;
        if (!value.StartsWith("dpapi:", StringComparison.Ordinal)) return false;
        try
        {
            var bytes = Convert.FromBase64String(value[6..]);
            token = Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
            return true;
        }
        catch { return false; }
    }

    private void QuarantineCorruptSettings()
    {
        if (!File.Exists(_settingsPath)) return;
        var quarantine = _settingsPath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        try { File.Move(_settingsPath, quarantine, false); } catch { }
    }
}

public enum ViewerSettingsLoadStatus
{
    Ok,
    Missing,
    NeedsPairing,
    Corrupt
}
