using System.Reflection;
using System.Text;
using System.Text.Json;

namespace SamsungSwitchWatch.Viewer.Services;

/// <summary>
/// Writes a minimal local diagnostic trail without accepting exception text,
/// device data, commands, or network identifiers.
/// </summary>
internal sealed class ViewerDiagnosticLog
{
    internal const long DefaultMaximumBytes = 1024 * 1024;
    internal const string CurrentFileName = "viewer-diagnostic.jsonl";
    internal const string BackupFileName = "viewer-diagnostic.1.jsonl";

    private static readonly object GlobalWriteLock = new();
    private static readonly UTF8Encoding Utf8WithoutBom = new(false);
    private static readonly HashSet<string> AllowedStages =
    [
        "app-initialize",
        "dispatcher-unhandled",
        "device-store-startup",
        "device-store-monitoring",
        "monitoring-store-startup",
        "monitoring-cycle",
        "settings-save-interactive",
        "settings-save-connection",
        "settings-save-background",
        "settings-save-shutdown",
        "client-dispose",
        "device-management-load",
        "device-management-save",
        "device-management-delete",
        "device-management-close"
    ];
    private static readonly HashSet<string> AllowedErrorCodes =
    [
        "VIEWER_MONITOR_STATE_CORRUPT",
        "VIEWER_MONITOR_STATE_VERSION_UNSUPPORTED",
        "VIEWER_MONITOR_STATE_UNAVAILABLE",
        "VIEWER_MONITOR_STATE_WRITE_FAILED",
        "VIEWER_MONITOR_CYCLE_FAILED",
        "VIEWER_SETTINGS_WRITE_FAILED",
        "VIEWER_CLIENT_DISPOSE_TIMEOUT",
        "VIEWER_CLIENT_DISPOSE_FAILED",
        "VIEWER_DEVICE_STORE_CORRUPT",
        "VIEWER_DEVICE_STORE_VERSION_UNSUPPORTED",
        "VIEWER_DEVICE_STORE_UNAVAILABLE",
        "VIEWER_DEVICE_STORE_WRITE_FAILED",
        "VIEWER_DEVICE_NOT_FOUND",
        "VIEWER_CREDENTIAL_CORRUPT",
        "VIEWER_UNEXPECTED_ERROR"
    ];
    private static readonly HashSet<string> AllowedConnectionStages =
    [
        "agent-http",
        "agent-realtime"
    ];
    private static readonly HashSet<string> AllowedConnectionErrorCodes =
    [
        "AGENT_ACCESS_DENIED",
        "AGENT_CLIENT_NOT_ALLOWED",
        "AGENT_CONNECTION_REFUSED",
        "AGENT_DNS_FAILED",
        "AGENT_HTTP_ERROR",
        "AGENT_IDENTITY_CHANGED",
        "AGENT_INTERNAL_ERROR",
        "AGENT_NOT_READY",
        "AGENT_PROTOCOL_MISMATCH",
        "AGENT_RESPONSE_INVALID",
        "AGENT_RESPONSE_TOO_LARGE",
        "AGENT_RESPONSE_UTF8_INVALID",
        "AGENT_TIMEOUT",
        "AGENT_UNREACHABLE",
        "VIEWER_CONFIGURATION_INVALID",
        "VIEWER_CONNECTION_REQUIRED",
        "VIEWER_UNEXPECTED_ERROR"
    ];
    private static readonly HashSet<string> AllowedConnectionTransitions =
    [
        "failed",
        "recovered"
    ];
    private readonly IViewerDiagnosticFileSystem _fileSystem;
    private readonly long _maximumBytes;
    private readonly string _applicationVersion;
    private readonly object _connectionTransitionLock = new();
    private readonly Dictionary<string, (string ErrorCode, string Transition)> _lastConnectionTransitions =
        new(StringComparer.Ordinal);

    public ViewerDiagnosticLog(
        string? directory = null,
        long maximumBytes = DefaultMaximumBytes,
        IViewerDiagnosticFileSystem? fileSystem = null,
        string? applicationVersion = null)
    {
        DirectoryPath = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SamsungSwitchWatch",
            "logs");
        CurrentPath = Path.Combine(DirectoryPath, CurrentFileName);
        BackupPath = Path.Combine(DirectoryPath, BackupFileName);
        _maximumBytes = Math.Max(256, maximumBytes);
        _fileSystem = fileSystem ?? PhysicalViewerDiagnosticFileSystem.Instance;
        _applicationVersion = NormalizeApplicationVersion(
            applicationVersion ?? ResolveApplicationVersion());
    }

    internal string DirectoryPath { get; }
    internal string CurrentPath { get; }
    internal string BackupPath { get; }

    public void Write(string stage, string errorCode)
    {
        var safeStage = AllowedStages.Contains(stage) ? stage : "diagnostic";
        var safeCode = AllowedErrorCodes.Contains(errorCode)
            ? errorCode
            : "VIEWER_UNEXPECTED_ERROR";
        var entry = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["timestampUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            ["appVersion"] = _applicationVersion,
            ["stage"] = safeStage,
            ["errorCode"] = safeCode
        });
        WriteLine(entry);
    }

    /// <summary>
    /// Records only allowlisted Agent connection failures and their recovery.
    /// Initial successful connections are intentionally omitted, and identical
    /// consecutive states are de-duplicated.
    /// </summary>
    public void WriteConnectionTransition(
        string stage,
        string errorCode,
        string transition)
    {
        var safeStage = AllowedConnectionStages.Contains(stage)
            ? stage
            : "agent-http";
        var safeCode = AllowedConnectionErrorCodes.Contains(errorCode)
            ? errorCode
            : "VIEWER_UNEXPECTED_ERROR";
        var safeTransition = AllowedConnectionTransitions.Contains(transition)
            ? transition
            : "failed";

        lock (_connectionTransitionLock)
        {
            if (safeTransition == "recovered")
            {
                if (!_lastConnectionTransitions.TryGetValue(safeStage, out var previous)
                    || previous.Transition != "failed")
                {
                    return;
                }
                safeCode = previous.ErrorCode;
            }

            var current = (safeCode, safeTransition);
            if (_lastConnectionTransitions.TryGetValue(safeStage, out var last)
                && last == current)
            {
                return;
            }

            var entry = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["timestampUtc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["appVersion"] = _applicationVersion,
                ["stage"] = safeStage,
                ["errorCode"] = safeCode,
                ["transition"] = safeTransition
            });
            if (WriteLine(entry))
            {
                _lastConnectionTransitions[safeStage] = current;
            }
        }
    }

    private bool WriteLine(string entry)
    {
        var line = entry + Environment.NewLine;
        var lineBytes = Utf8WithoutBom.GetByteCount(line);

        try
        {
            lock (GlobalWriteLock)
            {
                _fileSystem.CreateDirectory(DirectoryPath);
                if (_fileSystem.Exists(CurrentPath)
                    && _fileSystem.GetLength(CurrentPath) + lineBytes > _maximumBytes)
                {
                    _fileSystem.Move(CurrentPath, BackupPath, true);
                }
                _fileSystem.AppendAllText(CurrentPath, line, Utf8WithoutBom);
            }
            return true;
        }
        catch
        {
            // Diagnostics must never become an application failure path.
            return false;
        }
    }

    private static string ResolveApplicationVersion()
    {
        var assembly = typeof(ViewerDiagnosticLog).Assembly;
        return assembly
                   .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }

    private static string NormalizeApplicationVersion(string? value)
    {
        var candidate = value?.Trim() ?? string.Empty;
        var metadataIndex = candidate.IndexOf('+');
        if (metadataIndex >= 0)
        {
            candidate = candidate[..metadataIndex];
        }

        return IsSafeApplicationVersion(candidate)
            ? candidate
            : "unknown";
    }

    private static bool IsSafeApplicationVersion(string candidate)
    {
        if (candidate.Length is 0 or > 64)
        {
            return false;
        }

        var prereleaseIndex = candidate.IndexOf('-');
        var core = prereleaseIndex >= 0
            ? candidate[..prereleaseIndex]
            : candidate;
        var prerelease = prereleaseIndex >= 0
            ? candidate[(prereleaseIndex + 1)..]
            : null;
        var coreParts = core.Split('.');
        if (coreParts.Length is < 2 or > 4
            || coreParts.Any(part =>
                part.Length is 0 or > 10
                || part.Any(character => character is < '0' or > '9')))
        {
            return false;
        }

        return prerelease is null
               || (prerelease.Length > 0
                   && prerelease.All(character =>
                       character is >= 'A' and <= 'Z'
                           or >= 'a' and <= 'z'
                           or >= '0' and <= '9'
                           or '.'
                           or '-'));
    }
}

internal interface IViewerDiagnosticFileSystem
{
    void CreateDirectory(string path);
    bool Exists(string path);
    long GetLength(string path);
    void Move(string source, string destination, bool overwrite);
    void AppendAllText(string path, string content, Encoding encoding);
}

internal sealed class PhysicalViewerDiagnosticFileSystem : IViewerDiagnosticFileSystem
{
    public static PhysicalViewerDiagnosticFileSystem Instance { get; } = new();

    private PhysicalViewerDiagnosticFileSystem()
    {
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool Exists(string path) => File.Exists(path);

    public long GetLength(string path) => new FileInfo(path).Length;

    public void Move(string source, string destination, bool overwrite) =>
        File.Move(source, destination, overwrite);

    public void AppendAllText(string path, string content, Encoding encoding) =>
        File.AppendAllText(path, content, encoding);
}
