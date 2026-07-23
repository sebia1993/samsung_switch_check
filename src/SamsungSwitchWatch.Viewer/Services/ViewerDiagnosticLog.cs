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
        "monitoring-store-startup"
    ];
    private static readonly HashSet<string> AllowedErrorCodes =
    [
        "VIEWER_MONITOR_STATE_WRITE_FAILED",
        "VIEWER_UNEXPECTED_ERROR"
    ];
    private readonly IViewerDiagnosticFileSystem _fileSystem;
    private readonly long _maximumBytes;

    public ViewerDiagnosticLog(
        string? directory = null,
        long maximumBytes = DefaultMaximumBytes,
        IViewerDiagnosticFileSystem? fileSystem = null)
    {
        DirectoryPath = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SamsungSwitchWatch",
            "logs");
        CurrentPath = Path.Combine(DirectoryPath, CurrentFileName);
        BackupPath = Path.Combine(DirectoryPath, BackupFileName);
        _maximumBytes = Math.Max(256, maximumBytes);
        _fileSystem = fileSystem ?? PhysicalViewerDiagnosticFileSystem.Instance;
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
            ["stage"] = safeStage,
            ["errorCode"] = safeCode
        });
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
        }
        catch
        {
            // Diagnostics must never become an application failure path.
        }
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
