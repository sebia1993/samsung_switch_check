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
            var log = new ViewerDiagnosticLog(
                folder,
                applicationVersion: "1.2.3-poc+private-build-metadata");
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
            Assert.Equal(4, rejected.RootElement.EnumerateObject().Count());
            Assert.Equal(
                "1.2.3-poc",
                rejected.RootElement.GetProperty("appVersion").GetString());
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
            Assert.Equal(4, monitoringCycle.RootElement.EnumerateObject().Count());
            Assert.Equal(
                "monitoring-cycle",
                monitoringCycle.RootElement.GetProperty("stage").GetString());
            Assert.Equal(
                "VIEWER_MONITOR_CYCLE_FAILED",
                monitoringCycle.RootElement.GetProperty("errorCode").GetString());

            using var settingsSave = JsonDocument.Parse(lines[4]);
            Assert.Equal(4, settingsSave.RootElement.EnumerateObject().Count());
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
    public void DiagnosticLog_PreservesMonitorStateRecoveryCodesWithoutSensitiveContext()
    {
        var folder = TemporaryFolder();
        try
        {
            var log = new ViewerDiagnosticLog(
                folder,
                applicationVersion: "password=login-secret");
            var expectedCodes = new[]
            {
                "VIEWER_MONITOR_STATE_CORRUPT",
                "VIEWER_MONITOR_STATE_VERSION_UNSUPPORTED",
                "VIEWER_MONITOR_STATE_UNAVAILABLE"
            };

            foreach (var code in expectedCodes)
            {
                log.Write(
                    "monitoring-store-startup host=192.0.2.10 user=operator command=show-port-status",
                    code);
            }

            var content = File.ReadAllText(log.CurrentPath);
            Assert.DoesNotContain("192.0.2.10", content, StringComparison.Ordinal);
            Assert.DoesNotContain("operator", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("login-secret", content, StringComparison.Ordinal);
            Assert.DoesNotContain("show-port-status", content, StringComparison.OrdinalIgnoreCase);

            var lines = File.ReadAllLines(log.CurrentPath);
            Assert.Equal(expectedCodes.Length, lines.Length);
            for (var index = 0; index < expectedCodes.Length; index++)
            {
                using var document = JsonDocument.Parse(lines[index]);
                Assert.Equal(4, document.RootElement.EnumerateObject().Count());
                Assert.Equal(
                    "diagnostic",
                    document.RootElement.GetProperty("stage").GetString());
                Assert.Equal(
                    expectedCodes[index],
                    document.RootElement.GetProperty("errorCode").GetString());
                Assert.Equal(
                    "unknown",
                    document.RootElement.GetProperty("appVersion").GetString());
            }
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
    public void DiagnosticLog_AcceptsDeviceManagementStagesAndStableCodes()
    {
        var folder = TemporaryFolder();
        try
        {
            var log = new ViewerDiagnosticLog(folder);
            log.Write("device-management-load", "VIEWER_DEVICE_STORE_UNAVAILABLE");
            log.Write("device-management-save", "VIEWER_DEVICE_STORE_WRITE_FAILED");
            log.Write("device-management-delete", "VIEWER_DEVICE_NOT_FOUND");
            log.Write("device-management-close", "VIEWER_DEVICE_STORE_CORRUPT");
            log.Write(
                "device-management-load",
                "VIEWER_DEVICE_STORE_VERSION_UNSUPPORTED");

            var lines = File.ReadAllLines(log.CurrentPath);
            Assert.Equal(5, lines.Length);
            var expected = new[]
            {
                ("device-management-load", "VIEWER_DEVICE_STORE_UNAVAILABLE"),
                ("device-management-save", "VIEWER_DEVICE_STORE_WRITE_FAILED"),
                ("device-management-delete", "VIEWER_DEVICE_NOT_FOUND"),
                ("device-management-close", "VIEWER_DEVICE_STORE_CORRUPT"),
                (
                    "device-management-load",
                    "VIEWER_DEVICE_STORE_VERSION_UNSUPPORTED")
            };
            for (var index = 0; index < expected.Length; index++)
            {
                using var document = JsonDocument.Parse(lines[index]);
                Assert.Equal(4, document.RootElement.EnumerateObject().Count());
                Assert.Equal(
                    expected[index].Item1,
                    document.RootElement.GetProperty("stage").GetString());
                Assert.Equal(
                    expected[index].Item2,
                    document.RootElement.GetProperty("errorCode").GetString());
            }
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void DiagnosticLog_PreservesDeviceStoreLifecycleStagesWithoutSensitiveContext()
    {
        var folder = TemporaryFolder();
        try
        {
            var log = new ViewerDiagnosticLog(
                folder,
                applicationVersion: "password=login-secret");
            var expected = new[]
            {
                (
                    Stage: "device-store-startup",
                    ErrorCode: "VIEWER_DEVICE_STORE_VERSION_UNSUPPORTED"),
                (
                    Stage: "device-store-monitoring",
                    ErrorCode: "VIEWER_DEVICE_STORE_UNAVAILABLE")
            };

            foreach (var entry in expected)
            {
                log.Write(entry.Stage, entry.ErrorCode);
            }

            var content = File.ReadAllText(log.CurrentPath);
            Assert.DoesNotContain("login-secret", content, StringComparison.Ordinal);
            Assert.DoesNotContain("password", content, StringComparison.OrdinalIgnoreCase);

            var lines = File.ReadAllLines(log.CurrentPath);
            Assert.Equal(expected.Length, lines.Length);
            for (var index = 0; index < expected.Length; index++)
            {
                using var document = JsonDocument.Parse(lines[index]);
                Assert.Equal(4, document.RootElement.EnumerateObject().Count());
                Assert.Equal(
                    expected[index].Stage,
                    document.RootElement.GetProperty("stage").GetString());
                Assert.Equal(
                    expected[index].ErrorCode,
                    document.RootElement.GetProperty("errorCode").GetString());
                Assert.Equal(
                    "unknown",
                    document.RootElement.GetProperty("appVersion").GetString());
                Assert.True(document.RootElement.TryGetProperty("timestampUtc", out _));
            }
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
    public void ConnectionTransitions_DeduplicateAndWriteRecoveryOnlyAfterFailure()
    {
        var folder = TemporaryFolder();
        try
        {
            var log = new ViewerDiagnosticLog(
                folder,
                applicationVersion: "1.2.3-poc");

            log.WriteConnectionTransition("agent-http", "AGENT_CONNECTED", "recovered");
            log.WriteConnectionTransition(
                "agent-http",
                "AGENT_CONNECTION_REFUSED",
                "failed");
            log.WriteConnectionTransition(
                "agent-http",
                "AGENT_CONNECTION_REFUSED",
                "failed");
            log.WriteConnectionTransition("agent-http", "AGENT_CONNECTED", "recovered");
            log.WriteConnectionTransition("agent-http", "AGENT_CONNECTED", "recovered");

            var lines = File.ReadAllLines(log.CurrentPath);
            Assert.Equal(2, lines.Length);

            using var failure = JsonDocument.Parse(lines[0]);
            Assert.Equal(5, failure.RootElement.EnumerateObject().Count());
            Assert.Equal(
                "1.2.3-poc",
                failure.RootElement.GetProperty("appVersion").GetString());
            Assert.Equal(
                "agent-http",
                failure.RootElement.GetProperty("stage").GetString());
            Assert.Equal(
                "AGENT_CONNECTION_REFUSED",
                failure.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(
                "failed",
                failure.RootElement.GetProperty("transition").GetString());

            using var recovery = JsonDocument.Parse(lines[1]);
            Assert.Equal(5, recovery.RootElement.EnumerateObject().Count());
            Assert.Equal(
                "agent-http",
                recovery.RootElement.GetProperty("stage").GetString());
            Assert.Equal(
                "AGENT_CONNECTION_REFUSED",
                recovery.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(
                "recovered",
                recovery.RootElement.GetProperty("transition").GetString());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ConnectionTransitions_RejectSensitiveFieldsAndUnknownValues()
    {
        var folder = TemporaryFolder();
        try
        {
            var log = new ViewerDiagnosticLog(
                folder,
                applicationVersion: "password=login-secret");

            log.WriteConnectionTransition(
                "host=192.0.2.10 user=operator",
                "PASSWORD_LOGIN_SECRET",
                "raw-command-output");

            var content = File.ReadAllText(log.CurrentPath);
            Assert.DoesNotContain("192.0.2.10", content, StringComparison.Ordinal);
            Assert.DoesNotContain("operator", content, StringComparison.Ordinal);
            Assert.DoesNotContain("login-secret", content, StringComparison.Ordinal);
            Assert.DoesNotContain("PASSWORD_LOGIN_SECRET", content, StringComparison.Ordinal);
            Assert.DoesNotContain("raw-command-output", content, StringComparison.Ordinal);

            using var document = JsonDocument.Parse(content);
            Assert.Equal("unknown", document.RootElement.GetProperty("appVersion").GetString());
            Assert.Equal("agent-http", document.RootElement.GetProperty("stage").GetString());
            Assert.Equal(
                "VIEWER_UNEXPECTED_ERROR",
                document.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal("failed", document.RootElement.GetProperty("transition").GetString());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void ConnectionTransitions_PreserveViewerIpNotAllowedCode()
    {
        var folder = TemporaryFolder();
        try
        {
            var log = new ViewerDiagnosticLog(folder);

            log.WriteConnectionTransition(
                "agent-http",
                "AGENT_CLIENT_NOT_ALLOWED",
                "failed");

            using var document = JsonDocument.Parse(File.ReadAllText(log.CurrentPath));
            Assert.Equal(
                "AGENT_CLIENT_NOT_ALLOWED",
                document.RootElement.GetProperty("errorCode").GetString());
        }
        finally
        {
            Directory.Delete(folder, true);
        }
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
