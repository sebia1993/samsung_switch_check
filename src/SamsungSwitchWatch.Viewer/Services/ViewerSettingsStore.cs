using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SamsungSwitchWatch.Viewer.Services;

public sealed class ViewerSettings
{
    public bool DemoMode { get; set; } = true;
    public string AgentUri { get; set; } = "http://localhost:18443";
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

    public string BuildAgentIdentity(string agentId)
    {
        var normalizedUri = ViewerSettingsSanitizer.NormalizeAgentUri(AgentUri);
        var material = $"{agentId.Trim()}\n{normalizedUri.ToUpperInvariant()}";
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
    public const int DefaultAgentPort = 18443;

    public static ViewerSettings Sanitize(ViewerSettings? input)
    {
        input ??= new ViewerSettings();
        return new ViewerSettings
        {
            DemoMode = input.DemoMode,
            AgentUri = NormalizeAgentUri(input.AgentUri),
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

    public static ViewerSettings Copy(ViewerSettings source) => new()
    {
        DemoMode = source.DemoMode,
        AgentUri = source.AgentUri,
        LastEventSequence = source.LastEventSequence,
        EventCursors = new Dictionary<string, long>(source.EventCursors, StringComparer.Ordinal),
        MiniTopmost = source.MiniTopmost,
        MiniLeft = source.MiniLeft,
        MiniTop = source.MiniTop,
        MainLeft = source.MainLeft,
        MainTop = source.MainTop,
        MainWidth = source.MainWidth,
        MainHeight = source.MainHeight,
        StartMinimizedToTray = source.StartMinimizedToTray
    };

    public static bool TryBuildAgentUri(
        string? addressInput,
        string? portInput,
        out string agentUri,
        out string reason)
    {
        agentUri = string.Empty;
        var address = addressInput?.Trim() ?? string.Empty;
        if (!int.TryParse(portInput?.Trim(), out var port) || port is < 1 or > 65535)
        {
            reason = "포트는 1~65535 범위의 숫자여야 합니다.";
            return false;
        }
        if (!IsSupportedHost(address))
        {
            reason = "Agent 주소에는 IPv4 주소 또는 DNS 이름만 입력해 주세요.";
            return false;
        }

        try
        {
            agentUri = new UriBuilder(Uri.UriSchemeHttp, address, port).Uri
                .GetLeftPart(UriPartial.Authority)
                .TrimEnd('/');
            reason = string.Empty;
            return true;
        }
        catch (UriFormatException)
        {
            reason = "Agent 주소 형식이 올바르지 않습니다.";
            return false;
        }
    }

    public static void SplitAgentUri(string? value, out string address, out int port)
    {
        var normalized = NormalizeAgentUri(value);
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            address = uri.Host;
            port = uri.Port;
            return;
        }

        address = "localhost";
        port = DefaultAgentPort;
    }

    public static bool IsValidForLiveConnection(ViewerSettings settings, out string reason)
    {
        var normalized = NormalizeAgentUri(settings.AgentUri);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || uri.Port is < 1 or > 65535
            || !IsSupportedHost(uri.Host))
        {
            reason = "Agent 주소와 포트를 확인해 주세요.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public static string NormalizeAgentUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            || uri.Port is < 1 or > 65535
            || !IsSupportedHost(uri.Host))
        {
            return string.Empty;
        }

        try
        {
            // v0.5.x settings used HTTPS. Preserve the same host and effective
            // port while moving the connection to the v0.6 HTTP transport.
            return new UriBuilder(Uri.UriSchemeHttp, uri.Host, uri.Port).Uri
                .GetLeftPart(UriPartial.Authority)
                .TrimEnd('/');
        }
        catch (UriFormatException)
        {
            return string.Empty;
        }
    }

    private static bool IsSupportedHost(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains("://", StringComparison.Ordinal)
            || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        if (IPAddress.TryParse(value, out var address))
        {
            return address.AddressFamily == AddressFamily.InterNetwork;
        }

        return Uri.CheckHostName(value) == UriHostNameType.Dns;
    }

    private static double NormalizeCoordinate(double value) =>
        IsFinite(value) ? Math.Clamp(value, -32000, 32000) : -32000;

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
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

            // Legacy certificate and token properties are intentionally ignored by
            // System.Text.Json. A subsequent Save writes only the v0.6 HTTP model.
            var storedJson = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<ViewerSettings>(storedJson, JsonOptions);
            settings = ViewerSettingsSanitizer.Sanitize(settings);
            var migratedJson = JsonSerializer.Serialize(settings, JsonOptions);
            if (!string.Equals(storedJson, migratedJson, StringComparison.Ordinal))
            {
                try { Save(settings); }
                catch
                {
                    // A read-only profile must still be able to monitor with the
                    // in-memory migrated settings. The next writable save retries.
                }
            }
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
    NeedsConnection,
    Corrupt
}
