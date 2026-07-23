using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SamsungSwitchWatch.Viewer.Services;

public sealed class ViewerSettings
{
    public bool DemoMode { get; set; }
    public string AgentUri { get; set; } = "https://localhost:18443";
    public Dictionary<string, string> AgentTrustPins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
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

    public string BuildAgentAuthority()
    {
        var normalizedUri = ViewerSettingsSanitizer.NormalizeAgentUri(AgentUri);
        return Uri.TryCreate(normalizedUri, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Authority).ToUpperInvariant()
            : string.Empty;
    }

    public bool TryGetAgentTrustPin(out string pin) =>
        AgentTrustPins.TryGetValue(BuildAgentAuthority(), out pin!);

    public void SetAgentTrustPin(string pin)
    {
        var authority = BuildAgentAuthority();
        if (authority.Length == 0) throw new InvalidOperationException("VIEWER_CONNECTION_REQUIRED");
        AgentTrustPins[authority] = pin;
    }

    public void RemoveAgentTrustPin()
    {
        var authority = BuildAgentAuthority();
        if (authority.Length > 0) AgentTrustPins.Remove(authority);
    }

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
            AgentTrustPins = (input.AgentTrustPins ?? new Dictionary<string, string>())
                .Where(item => item.Key.Length is > 0 and <= 256 && IsSha256Hex(item.Value))
                .Take(32)
                .ToDictionary(item => item.Key, item => item.Value.ToUpperInvariant(), StringComparer.OrdinalIgnoreCase),
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
        AgentTrustPins = new Dictionary<string, string>(source.AgentTrustPins, StringComparer.OrdinalIgnoreCase),
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
        if (!int.TryParse(portInput?.Trim(), out var port) || port != DefaultAgentPort)
        {
            reason = $"Agent HTTPS 포트는 {DefaultAgentPort}을 사용합니다.";
            return false;
        }
        if (!IsSupportedHost(address))
        {
            reason = "Agent 주소에는 IPv4 주소 또는 DNS 이름만 입력해 주세요.";
            return false;
        }

        try
        {
            agentUri = new UriBuilder(Uri.UriSchemeHttps, address, port).Uri
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
            port = DefaultAgentPort;
            return;
        }

        address = "localhost";
        port = DefaultAgentPort;
    }

    public static bool IsValidForLiveConnection(ViewerSettings settings, out string reason)
    {
        var normalized = NormalizeAgentUri(settings.AgentUri);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Port != DefaultAgentPort
            || !IsSupportedHost(uri.Host))
        {
            reason = "Agent 주소를 확인해 주세요.";
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
            return new UriBuilder(Uri.UriSchemeHttps, uri.Host, DefaultAgentPort).Uri
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

    private static bool IsSha256Hex(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9'
                or >= 'A' and <= 'F'
                or >= 'a' and <= 'f');
}

public sealed class ViewerSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;
    private readonly IViewerSettingsPersistence _persistence;

    public ViewerSettingsLoadStatus LastLoadStatus { get; private set; } = ViewerSettingsLoadStatus.Ok;

    public ViewerSettingsStore(string? settingsPath = null)
        : this(settingsPath, PhysicalViewerSettingsPersistence.Instance)
    {
    }

    internal ViewerSettingsStore(
        string? settingsPath,
        IViewerSettingsPersistence persistence)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SamsungSwitchWatch",
            "viewer-settings.json");
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    }

    public string SettingsPath => _settingsPath;

    public ViewerSettings Load()
    {
        try
        {
            var storedJson = _persistence.ReadIfExists(_settingsPath);
            if (storedJson is null)
            {
                LastLoadStatus = ViewerSettingsLoadStatus.Missing;
                return new ViewerSettings();
            }

            // Legacy manually-entered fingerprint and token fields are ignored.
            // v0.8 and later store only automatic per-Agent trust pins.
            var settings = JsonSerializer.Deserialize<ViewerSettings>(storedJson, JsonOptions)
                           ?? throw new ViewerSettingsFormatException();
            settings = ViewerSettingsSanitizer.Sanitize(settings);
            var migratedJson = JsonSerializer.Serialize(settings, JsonOptions);
            if (!string.Equals(storedJson, migratedJson, StringComparison.Ordinal))
            {
                try
                {
                    Save(settings);
                }
                catch (Exception exception) when (IsStorageException(exception))
                {
                    // A read-only profile must still be able to monitor with the
                    // in-memory migrated settings. Report the failed persistence
                    // instead of claiming that migration completed successfully.
                    LastLoadStatus = ViewerSettingsLoadStatus.StorageUnavailable;
                    return settings;
                }
            }
            LastLoadStatus = ViewerSettingsLoadStatus.Ok;
            return settings;
        }
        catch (Exception exception) when (
            exception is JsonException
            or NotSupportedException
            or DecoderFallbackException
            or ViewerSettingsFormatException)
        {
            QuarantineCorruptSettings();
            LastLoadStatus = ViewerSettingsLoadStatus.Corrupt;
            return new ViewerSettings();
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            // An access or I/O failure is not evidence that the JSON is corrupt.
            // Keep the original file in place and start with safe in-memory
            // defaults so the connection screen can explain what needs attention.
            LastLoadStatus = ViewerSettingsLoadStatus.StorageUnavailable;
            return new ViewerSettings();
        }
    }

    public void Save(ViewerSettings settings)
    {
        var clean = ViewerSettingsSanitizer.Sanitize(settings);
        _persistence.WriteAtomically(_settingsPath, JsonSerializer.Serialize(clean, JsonOptions));
    }

    private void QuarantineCorruptSettings()
    {
        var quarantine = _settingsPath + $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}";
        try
        {
            _persistence.Quarantine(_settingsPath, quarantine);
        }
        catch (Exception exception) when (IsStorageException(exception))
        {
            // The source is still known to contain invalid JSON. If the file is
            // locked or read-only, leave it in place rather than deleting it.
        }
    }

    private static bool IsStorageException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException;

    private sealed class ViewerSettingsFormatException : Exception
    {
    }
}

public enum ViewerSettingsLoadStatus
{
    Ok,
    Missing,
    NeedsConnection,
    Corrupt,
    StorageUnavailable
}

internal interface IViewerSettingsPersistence
{
    string? ReadIfExists(string path);
    void WriteAtomically(string path, string content);
    void Quarantine(string path, string destination);
}

internal sealed class PhysicalViewerSettingsPersistence : IViewerSettingsPersistence
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static PhysicalViewerSettingsPersistence Instance { get; } = new();

    private PhysicalViewerSettingsPersistence()
    {
    }

    public string? ReadIfExists(string path)
    {
        try
        {
            return File.ReadAllText(path, StrictUtf8);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public void WriteAtomically(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
                        ?? throw new InvalidOperationException("VIEWER_SETTINGS_PATH_INVALID");
        Directory.CreateDirectory(directory);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // Best-effort cleanup only. The destination write result is
                // already determined and the previous file remains intact.
            }
            catch (UnauthorizedAccessException)
            {
                // See IOException comment above.
            }
        }
    }

    public void Quarantine(string path, string destination) =>
        File.Move(path, destination, false);
}
