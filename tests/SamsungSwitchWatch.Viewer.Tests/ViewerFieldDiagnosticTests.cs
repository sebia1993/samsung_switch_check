using System.IO;
using System.Text;
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

        Assert.StartsWith("SSW_FIELD_DIAGNOSTIC/1\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Component=VIEWER\r\n", text, StringComparison.Ordinal);
        Assert.Contains("ProductVersion=UNKNOWN\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Mode=SAME_PC\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Operation=AGENT_CONNECTION_CHECK\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Result=SUCCESS\r\n", text, StringComparison.Ordinal);
        Assert.Contains("FailedStage=NONE\r\n", text, StringComparison.Ordinal);
        Assert.Contains("ErrorCode=NONE\r\n", text, StringComparison.Ordinal);
        Assert.Contains("RecommendedActionCode=NONE\r\n", text, StringComparison.Ordinal);
        Assert.Contains("AddressStatus=SUCCEEDED\r\n", text, StringComparison.Ordinal);
        Assert.Contains("IdentityDurationMs=5\r\n", text, StringComparison.Ordinal);
        Assert.Contains("CandidateCount=2\r\n", text, StringComparison.Ordinal);
        Assert.Contains("AgentProductVersion=0.10.8-poc\r\n", text, StringComparison.Ordinal);
        Assert.Contains("ApiVersion=4\r\n", text, StringComparison.Ordinal);
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

        Assert.Contains("Mode=NORMAL\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Result=FAILED\r\n", text, StringComparison.Ordinal);
        Assert.Contains("FailedStage=TCP\r\n", text, StringComparison.Ordinal);
        Assert.Contains("ErrorCode=AGENT_CONNECTION_REFUSED\r\n", text, StringComparison.Ordinal);
        Assert.Contains("RecommendedActionCode=CHECK_AGENT_SERVICE\r\n", text, StringComparison.Ordinal);
        Assert.Contains("TcpStatus=FAILED\r\n", text, StringComparison.Ordinal);
        Assert.Contains("TcpDurationMs=0\r\n", text, StringComparison.Ordinal);
        Assert.Contains("HttpsStatus=NOT_RUN\r\n", text, StringComparison.Ordinal);
        Assert.Contains(
            $"CandidateCount={LocalAgentPreflight.DefaultMaxCandidateAttempts}\r\n",
            text,
            StringComparison.Ordinal);
        Assert.Contains("WindowsBuild=UNKNOWN\r\n", text, StringComparison.Ordinal);
        Assert.Contains("Architecture=UNKNOWN\r\n", text, StringComparison.Ordinal);
        Assert.Contains("AgentProductVersion=UNKNOWN\r\n", text, StringComparison.Ordinal);
        Assert.Contains("ApiVersion=UNKNOWN\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Detail, text, StringComparison.Ordinal);
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

        Assert.Equal("FAILED", snapshot.Result);
        Assert.Equal(expectedStage, snapshot.FailedStage);
        Assert.Equal(errorCode, snapshot.ErrorCode);
        Assert.Contains($"FailedStage={expectedStage}\r\n", text, StringComparison.Ordinal);
        Assert.Contains("AddressDurationMs=11\r\n", text, StringComparison.Ordinal);
        Assert.Contains("IdentityDurationMs=15\r\n", text, StringComparison.Ordinal);
        Assert.Contains("AgentProductVersion=0.11.0-poc\r\n", text, StringComparison.Ordinal);
        Assert.Contains("ApiVersion=4\r\n", text, StringComparison.Ordinal);
        if (errorCode == "VIEWER_SETTINGS_WRITE_FAILED")
        {
            Assert.Equal("CHECK_VIEWER_STORAGE", snapshot.RecommendedActionCode);
            Assert.Contains(
                "RecommendedActionCode=CHECK_VIEWER_STORAGE\r\n",
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
        Assert.Contains("AgentProductVersion=0.10.9-poc\r\n", text, StringComparison.Ordinal);
        Assert.Contains("ApiVersion=4\r\n", text, StringComparison.Ordinal);
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
        Assert.Contains("AgentProductVersion=UNKNOWN\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain(untrustedVersion, text, StringComparison.Ordinal);
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
}
