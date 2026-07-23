using System.IO;
using System.Text;
using System.Text.Json;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerDiagnosticLogTests
{
    [Fact]
    public void DiagnosticLog_WritesOnlyTimestampStageAndStableCode()
    {
        var folder = TemporaryFolder();
        try
        {
            var log = new ViewerDiagnosticLog(folder);
            log.Write(
                "startup host=192.0.2.10 user=operator command=show-running-config",
                "PASSWORD=login-secret");
            log.Write("operator", "LOGIN_SECRET");
            log.Write("monitoring-store-startup", "VIEWER_MONITOR_STATE_WRITE_FAILED");
            log.Write("monitoring-cycle", "VIEWER_MONITOR_CYCLE_FAILED");
            log.Write("settings-save-background", "VIEWER_SETTINGS_WRITE_FAILED");

            var bytes = File.ReadAllBytes(log.CurrentPath);
            Assert.False(bytes.Length >= 3
                         && bytes[0] == 0xEF
                         && bytes[1] == 0xBB
                         && bytes[2] == 0xBF);
            var content = Encoding.UTF8.GetString(bytes);
            Assert.DoesNotContain("192.0.2.10", content, StringComparison.Ordinal);
            Assert.DoesNotContain("operator", content, StringComparison.Ordinal);
            Assert.DoesNotContain("show-running-config", content, StringComparison.Ordinal);
            Assert.DoesNotContain("login-secret", content, StringComparison.Ordinal);
            Assert.DoesNotContain("LOGIN_SECRET", content, StringComparison.Ordinal);
            Assert.DoesNotContain("PASSWORD", content, StringComparison.Ordinal);

            var lines = File.ReadAllLines(log.CurrentPath);
            Assert.Equal(5, lines.Length);
            using var rejected = JsonDocument.Parse(lines[0]);
            Assert.Equal(3, rejected.RootElement.EnumerateObject().Count());
            Assert.Equal("diagnostic", rejected.RootElement.GetProperty("stage").GetString());
            Assert.Equal(
                "VIEWER_UNEXPECTED_ERROR",
                rejected.RootElement.GetProperty("errorCode").GetString());
            Assert.True(rejected.RootElement.TryGetProperty("timestampUtc", out _));

            using var stableLookingSecret = JsonDocument.Parse(lines[1]);
            Assert.Equal(
                "diagnostic",
                stableLookingSecret.RootElement.GetProperty("stage").GetString());
            Assert.Equal(
                "VIEWER_UNEXPECTED_ERROR",
                stableLookingSecret.RootElement.GetProperty("errorCode").GetString());

            using var accepted = JsonDocument.Parse(lines[2]);
            Assert.Equal(
                "monitoring-store-startup",
                accepted.RootElement.GetProperty("stage").GetString());
            Assert.Equal(
                "VIEWER_MONITOR_STATE_WRITE_FAILED",
                accepted.RootElement.GetProperty("errorCode").GetString());

            using var monitoringCycle = JsonDocument.Parse(lines[3]);
            Assert.Equal(3, monitoringCycle.RootElement.EnumerateObject().Count());
            Assert.Equal(
                "monitoring-cycle",
                monitoringCycle.RootElement.GetProperty("stage").GetString());
            Assert.Equal(
                "VIEWER_MONITOR_CYCLE_FAILED",
                monitoringCycle.RootElement.GetProperty("errorCode").GetString());

            using var settingsSave = JsonDocument.Parse(lines[4]);
            Assert.Equal(3, settingsSave.RootElement.EnumerateObject().Count());
            Assert.Equal(
                "settings-save-background",
                settingsSave.RootElement.GetProperty("stage").GetString());
            Assert.Equal(
                "VIEWER_SETTINGS_WRITE_FAILED",
                settingsSave.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void DiagnosticLog_RotatesToOneBackupAtConfiguredLimit()
    {
        var folder = TemporaryFolder();
        try
        {
            const long maximumBytes = 256;
            var log = new ViewerDiagnosticLog(folder, maximumBytes);

            for (var index = 0; index < 30; index++)
            {
                log.Write("app-initialize", "VIEWER_UNEXPECTED_ERROR");
            }

            Assert.True(File.Exists(log.CurrentPath));
            Assert.True(File.Exists(log.BackupPath));
            Assert.InRange(new FileInfo(log.CurrentPath).Length, 1, maximumBytes);
            Assert.InRange(new FileInfo(log.BackupPath).Length, 1, maximumBytes);
            Assert.Equal(
                2,
                Directory.GetFiles(folder, "viewer-diagnostic*.jsonl").Length);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void DiagnosticLog_WriteFailureNeverEscapes()
    {
        var fileSystem = new ThrowingDiagnosticFileSystem();
        var log = new ViewerDiagnosticLog(
            "unavailable",
            ViewerDiagnosticLog.DefaultMaximumBytes,
            fileSystem);

        var exception = Record.Exception(
            () => log.Write("app-initialize", "VIEWER_UNEXPECTED_ERROR"));

        Assert.Null(exception);
        Assert.Equal(1, fileSystem.CreateDirectoryAttempts);
    }

    [Fact]
    public void DiagnosticLog_ConcurrentWritesProduceCompleteJsonLines()
    {
        var folder = TemporaryFolder();
        try
        {
            var log = new ViewerDiagnosticLog(folder);

            Parallel.For(
                0,
                200,
                _ => log.Write("dispatcher-unhandled", "VIEWER_UNEXPECTED_ERROR"));

            var lines = File.ReadAllLines(log.CurrentPath);
            Assert.Equal(200, lines.Length);
            foreach (var line in lines)
            {
                using var document = JsonDocument.Parse(line);
                Assert.Equal(
                    "dispatcher-unhandled",
                    document.RootElement.GetProperty("stage").GetString());
                Assert.Equal(
                    "VIEWER_UNEXPECTED_ERROR",
                    document.RootElement.GetProperty("errorCode").GetString());
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static string TemporaryFolder()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "SamsungSwitchWatch-DiagnosticLog",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ThrowingDiagnosticFileSystem : IViewerDiagnosticFileSystem
    {
        public int CreateDirectoryAttempts { get; private set; }

        public void CreateDirectory(string path)
        {
            CreateDirectoryAttempts++;
            throw new UnauthorizedAccessException("simulated");
        }

        public bool Exists(string path) => throw new InvalidOperationException("not reached");

        public long GetLength(string path) => throw new InvalidOperationException("not reached");

        public void Move(string source, string destination, bool overwrite) =>
            throw new InvalidOperationException("not reached");

        public void AppendAllText(string path, string content, Encoding encoding) =>
            throw new InvalidOperationException("not reached");
    }
}
