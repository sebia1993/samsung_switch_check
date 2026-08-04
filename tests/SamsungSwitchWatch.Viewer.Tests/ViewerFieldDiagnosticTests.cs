using System.IO;
using System.Text;
using SamsungSwitchWatch.Support;
using SamsungSwitchWatch.Viewer.Models;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Tests;

public sealed class ViewerFieldDiagnosticTests
{
    [Fact]
    public void Format_UsesBoundedAllowlistedFieldsAndOmitsSensitiveProbeData()
    {
        var identity = new AgentIdentityDto(
            4,
            "operator-pc",
            "instance-secret",
            new string('F', 64),
            "https",
            8,
            65_536)
        {
            ProductVersion = "0.10.8-poc"
        };
        var result = AgentConnectionProbeResult.Success(
                identity,
                "password=hunter2 https://192.0.2.10 C:\\Users\\operator show sylog tail num 100")
            with
        {
            StageSnapshots =
                [
                    new(AgentConnectionProbeStage.Address, AgentConnectionProbeState.Succeeded, 1),
                    new(AgentConnectionProbeStage.Dns, AgentConnectionProbeState.Succeeded, 2),
                    new(AgentConnectionProbeStage.Tcp, AgentConnectionProbeState.Succeeded, 3),
                    new(AgentConnectionProbeStage.Https, AgentConnectionProbeState.Succeeded, 4),
                    new(AgentConnectionProbeStage.Identity, AgentConnectionProbeState.Succeeded, 5)
                ]
        };

        var snapshot = ViewerFieldDiagnostic.Create(
            "SAME_PC",
            result,
            candidateCount: 2,
            generatedUtc: new DateTimeOffset(2026, 7, 29, 1, 2, 3, TimeSpan.Zero),
            productVersion: @"C:\Users\operator 192.0.2.10",
            windowsBuild: "22631",
            architecture: "x64");
        var text = ViewerFieldDiagnostic.Format(snapshot);

        AssertCompactPhotoFormat(text);
        Assert.StartsWith("SSW_FIELD_DIAGNOSTIC/2\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Component=VIEWER\r\n", text, StringComparison.Ordinal);
        Assert.Contains("ProductVersion=UNKNOWN\r\n", text, StringComparison.Ordinal);
        Assert.Contains(
            "Environment=2026-07-29T01:02:03.0000000+00:00|22631|X64\r\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "Run=SAME_PC|AGENT_CONNECTION_CHECK|SUCCESS\r\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("FailedStage=NONE\r\n", text, StringComparison.Ordinal);
        Assert.Contains("ErrorCode=NONE\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Action=NONE\r\n", text, StringComparison.Ordinal);
        Assert.Contains(
            "Stages=ADDR:OK|DNS:OK|TCP:OK|HTTPS:OK|ID:OK\r\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("TimingMs=1|2|3|4|5\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Agent=2|0.10.8-poc|4\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("192.0.2.10", text, StringComparison.Ordinal);
        Assert.DoesNotContain("operator", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("show sylog", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(new string('F', 64), text, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_FailureNormalizesMissingTimingAndMapsRecommendedAction()
    {
        var result = AgentConnectionProbeResult.Failure(
            AgentConnectionProbeStage.Tcp,
            "AGENT_CONNECTION_REFUSED",
            "raw exception and address must not be exported");

        var snapshot = ViewerFieldDiagnostic.Create(
            "NORMAL",
            result,
            candidateCount: int.MaxValue,
            generatedUtc: DateTimeOffset.UnixEpoch,
            productVersion: "0.10.8-poc",
            windowsBuild: "not-a-build",
            architecture: "unknown-cpu");
        var text = ViewerFieldDiagnostic.Format(snapshot);

        AssertCompactPhotoFormat(text);
        Assert.Contains(
            "Run=NORMAL|AGENT_CONNECTION_CHECK|FAILED\r\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("FailedStage=TCP\r\n", text, StringComparison.Ordinal);
        Assert.Contains("ErrorCode=AGENT_CONNECTION_REFUSED\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Action=CHECK_AGENT_SERVICE\r\n", text, StringComparison.Ordinal);
        Assert.Contains(
            "Stages=ADDR:PENDING|DNS:PENDING|TCP:FAIL|HTTPS:SKIP|ID:SKIP\r\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("TimingMs=0|0|0|0|0\r\n", text, StringComparison.Ordinal);
        Assert.Contains(
            $"Agent={LocalAgentPreflight.DefaultMaxCandidateAttempts}|UNKNOWN|UNKNOWN\r\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            "Environment=1970-01-01T00:00:00.0000000+00:00|UNKNOWN|UNKNOWN\r\n",
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(result.Detail, text, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportCode_EncodesOnlySanitizedFailureSnapshot()
    {
        var result = AgentConnectionProbeResult.Failure(
            AgentConnectionProbeStage.Tcp,
            "AGENT_CONNECTION_REFUSED",
            @"DOMAIN\operator password=hunter2 10.20.30.40") with
        {
            StageSnapshots =
                [
                    new(
                        AgentConnectionProbeStage.Address,
                        AgentConnectionProbeState.Succeeded,
                        10),
                    new(
                        AgentConnectionProbeStage.Dns,
                        AgentConnectionProbeState.Succeeded,
                        20),
                    new(
                        AgentConnectionProbeStage.Tcp,
                        AgentConnectionProbeState.Failed,
                        30)
                ]
        };
        var snapshot = ViewerFieldDiagnostic.Create(
            "SAME_PC",
            result,
            candidateCount: 2,
            generatedUtc: DateTimeOffset.UnixEpoch,
            productVersion: "0.10.10-poc",
            windowsBuild: "22631",
            architecture: "x64");

        var code = ViewerFieldDiagnostic.CreateSupportCode(snapshot);

        Assert.Equal(24, code.Length);
        Assert.True(Swd1SupportCode.TryDecode(code, out var decoded));
        Assert.Equal(
            "AGENT_CONNECTION_REFUSED",
            decoded!.Common.ResultCodeName);
        Assert.Equal(Swd1ViewerMode.SamePc, decoded.Viewer!.Value.Mode);
        Assert.Equal(
            Swd1ViewerFailedStage.Tcp,
            decoded.Viewer.Value.FailedStage);
        Assert.Equal(2, decoded.Viewer.Value.CandidateCount);
        Assert.Equal(
            Swd1ViewerStageState.Failed,
            decoded.Viewer.Value.Stages.Tcp);
        Assert.DoesNotContain("operator", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hunter2", code, StringComparison.Ordinal);
        Assert.DoesNotContain("10.20.30.40", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportCode_RejectsSuccessfulSnapshot()
    {
        var snapshot = ViewerFieldDiagnostic.Create(
            "NORMAL",
            AgentConnectionProbeResult.Success(
                Identity("0.10.10-poc"),
                "done"),
            1,
            productVersion: "0.10.10-poc",
            windowsBuild: "22631",
            architecture: "x64");

        Assert.Throws<ArgumentException>(
            () => ViewerFieldDiagnostic.CreateSupportCode(snapshot));
    }

    [Theory]
    [InlineData("AGENT_IDENTITY_CHANGED", "HTTPS")]
    [InlineData("AGENT_PROTOCOL_MISMATCH", "HTTPS")]
    [InlineData("AGENT_DNS_FAILED", "DNS")]
    [InlineData("AGENT_CONNECTION_REFUSED", "TCP")]
    [InlineData("AGENT_TIMEOUT", "TCP")]
    [InlineData("AGENT_UNREACHABLE", "TCP")]
    [InlineData("VIEWER_SETTINGS_WRITE_FAILED", "SETTINGS")]
    [InlineData("AGENT_VERSION_MISMATCH", "IDENTITY")]
    public void CreateApplyFailure_PreservesProbeTimingAndMapsFailureStage(
        string errorCode,
        string expectedStage)
    {
        var identity = Identity("0.11.0-poc");
        var successfulProbe = AgentConnectionProbeResult.Success(identity, "not exported")
            with
        {
            StageSnapshots =
                [
                    new(AgentConnectionProbeStage.Address, AgentConnectionProbeState.Succeeded, 11),
                    new(AgentConnectionProbeStage.Dns, AgentConnectionProbeState.Succeeded, 12),
                    new(AgentConnectionProbeStage.Tcp, AgentConnectionProbeState.Succeeded, 13),
                    new(AgentConnectionProbeStage.Https, AgentConnectionProbeState.Succeeded, 14),
                    new(AgentConnectionProbeStage.Identity, AgentConnectionProbeState.Succeeded, 15)
                ]
        };

        var snapshot = ViewerFieldDiagnostic.CreateApplyFailure(
            "SAME_PC",
            successfulProbe,
            3,
            errorCode,
            generatedUtc: DateTimeOffset.UnixEpoch,
            productVersion: "0.11.0-poc",
            windowsBuild: "22631",
            architecture: "x64");
        var text = ViewerFieldDiagnostic.Format(snapshot);
        var supportCode =
            ViewerFieldDiagnostic.CreateSupportCode(snapshot);

        Assert.Equal("FAILED", snapshot.Result);
        Assert.Equal(expectedStage, snapshot.FailedStage);
        Assert.Equal(errorCode, snapshot.ErrorCode);
        AssertCompactPhotoFormat(text);
        Assert.Contains($"FailedStage={expectedStage}\r\n", text, StringComparison.Ordinal);
        Assert.Contains("TimingMs=11|12|13|14|15\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Agent=3|0.11.0-poc|4\r\n", text, StringComparison.Ordinal);
        Assert.True(
            Swd1SupportCode.TryDecode(
                supportCode,
                out var decoded));
        Assert.Equal(errorCode, decoded!.Common.ResultCodeName);
        Assert.Equal(
            expectedStage,
            decoded.Viewer!.Value.FailedStage
                .ToString()
                .ToUpperInvariant());
        Assert.Equal(Swd1ViewerMode.SamePc, decoded.Viewer.Value.Mode);
        Assert.Equal("0.11.0", decoded.Viewer.Value.AgentVersion.ToString());
        Assert.Equal(4, decoded.Viewer.Value.ApiVersion);
        Assert.Equal(
            Swd1ViewerStageState.Succeeded,
            decoded.Viewer.Value.Stages.Identity);
        if (errorCode == "VIEWER_SETTINGS_WRITE_FAILED")
        {
            Assert.Equal("CHECK_VIEWER_STORAGE", snapshot.RecommendedActionCode);
            Assert.Contains(
                "Action=CHECK_VIEWER_STORAGE\r\n",
                text,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Create_VersionMismatchFailureCarriesSafeConfirmedIdentity()
    {
        var result = AgentConnectionProbeResult.Failure(
            AgentConnectionProbeStage.Identity,
            "AGENT_VERSION_MISMATCH",
            "raw detail not exported",
            Identity("0.10.9-poc"));

        var snapshot = ViewerFieldDiagnostic.Create(
            "NORMAL",
            result,
            1,
            generatedUtc: DateTimeOffset.UnixEpoch,
            productVersion: "0.11.0-poc",
            windowsBuild: "22631",
            architecture: "x64");
        var text = ViewerFieldDiagnostic.Format(snapshot);

        Assert.Equal("FAILED", snapshot.Result);
        Assert.Equal("0.10.9-poc", snapshot.AgentProductVersion);
        Assert.Equal("4", snapshot.ApiVersion);
        AssertCompactPhotoFormat(text);
        Assert.Contains("Agent=1|0.10.9-poc|4\r\n", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("monitor-pc")]
    [InlineData("viewer_host")]
    [InlineData("0.10")]
    [InlineData("0.10.8-")]
    [InlineData("0.10.8-preview..1")]
    public void Format_RejectsNonProductVersionTextFromDiagnosticFields(
        string untrustedVersion)
    {
        var result = AgentConnectionProbeResult.Success(
            Identity(untrustedVersion),
            "not exported");
        var snapshot = ViewerFieldDiagnostic.Create(
            "NORMAL",
            result,
            1,
            generatedUtc: DateTimeOffset.UnixEpoch,
            productVersion: untrustedVersion,
            windowsBuild: "22631",
            architecture: "x64");
        var text = ViewerFieldDiagnostic.Format(snapshot);

        Assert.Equal("UNKNOWN", snapshot.ProductVersion);
        Assert.Equal("UNKNOWN", snapshot.AgentProductVersion);
        Assert.Contains("ProductVersion=UNKNOWN\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Agent=1|UNKNOWN|4\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(untrustedVersion, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_MaximumAllowedValuesStayWithinPhotoBounds()
    {
        var maximumVersion =
            "1234567890.1234567890.1234567890-1234567890123456789012345678901";
        var identity = new AgentIdentityDto(
            9_999,
            "not-exported",
            "not-exported",
            new string('B', 64),
            "https",
            8,
            65_536)
        {
            ProductVersion = maximumVersion
        };
        var result = AgentConnectionProbeResult.Success(identity, "not exported") with
        {
            StageSnapshots =
                [
                    new(AgentConnectionProbeStage.Address, AgentConnectionProbeState.Running, long.MaxValue),
                    new(AgentConnectionProbeStage.Dns, AgentConnectionProbeState.Pending, long.MinValue),
                    new(AgentConnectionProbeStage.Tcp, AgentConnectionProbeState.Succeeded, long.MaxValue),
                    new(AgentConnectionProbeStage.Https, AgentConnectionProbeState.Failed, long.MaxValue),
                    new(AgentConnectionProbeStage.Identity, AgentConnectionProbeState.Pending, long.MaxValue)
                ]
        };
        var snapshot = ViewerFieldDiagnostic.Create(
            "SAME_PC",
            result,
            int.MaxValue,
            generatedUtc: DateTimeOffset.MaxValue,
            productVersion: maximumVersion,
            windowsBuild: "999999",
            architecture: "arm64");

        var text = ViewerFieldDiagnostic.Format(snapshot);

        AssertCompactPhotoFormat(text);
        Assert.Contains($"ProductVersion={maximumVersion}\r\n", text, StringComparison.Ordinal);
        Assert.Contains(
            "Stages=ADDR:PENDING|DNS:PENDING|TCP:OK|HTTPS:FAIL|ID:PENDING\r\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains(
            $"Agent={LocalAgentPreflight.DefaultMaxCandidateAttempts}|{maximumVersion}|9999\r\n",
            text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Writer_WritesUtf8BomTxtAndReturnsStableFailureCode()
    {
        var folder = Path.Combine(
            Path.GetTempPath(),
            "SamsungSwitchWatch-ViewerDiagnostic",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var result = AgentConnectionProbeResult.Failure(
                AgentConnectionProbeStage.Dns,
                "AGENT_DNS_FAILED",
                "not exported");
            var snapshot = ViewerFieldDiagnostic.Create(
                "NORMAL",
                result,
                1,
                productVersion: "0.10.8-poc",
                windowsBuild: "22631",
                architecture: "x64");
            var path = Path.Combine(folder, "diagnostic.txt");
            var writer = new ViewerFieldDiagnosticWriter();

            var success = await writer.WriteAsync(path, snapshot);
            var bytes = await File.ReadAllBytesAsync(path);
            var failed = await writer.WriteAsync(
                Path.Combine(folder, "missing", "diagnostic.txt"),
                snapshot);

            Assert.True(success.Succeeded);
            Assert.Equal("DIAGNOSTIC_WRITE_OK", success.ErrorCode);
            Assert.True(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
            Assert.StartsWith(
                ViewerFieldDiagnostic.Schema,
                Encoding.UTF8.GetString(bytes.AsSpan(Encoding.UTF8.Preamble.Length)),
                StringComparison.Ordinal);
            Assert.False(failed.Succeeded);
            Assert.Equal("DIAGNOSTIC_WRITE_FAILED", failed.ErrorCode);
        }
        finally
        {
            Directory.Delete(folder, true);
        }
    }

    private static AgentIdentityDto Identity(string productVersion) =>
        new(
            4,
            "agent-test",
            "instance-test",
            new string('A', 64),
            "https",
            8,
            65_536)
        {
            ProductVersion = productVersion
        };

    private static void AssertCompactPhotoFormat(string text)
    {
        Assert.EndsWith("\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal));
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(11, lines.Length);
        Assert.All(lines, line => Assert.InRange(line.Length, 1, 88));
    }
}
