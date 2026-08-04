using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SamsungSwitchWatch.Support;
using SamsungSwitchWatch.Viewer.Models;

namespace SamsungSwitchWatch.Viewer.Services;

internal sealed record ViewerFieldDiagnosticSnapshot(
    DateTimeOffset GeneratedUtc,
    string ProductVersion,
    string WindowsBuild,
    string Architecture,
    string Mode,
    string Operation,
    string Result,
    string FailedStage,
    string ErrorCode,
    string RecommendedActionCode,
    int CandidateCount,
    string AgentProductVersion,
    string ApiVersion,
    IReadOnlyList<AgentConnectionProbeStageSnapshot> Stages);

internal static class ViewerFieldDiagnostic
{
    internal const string Schema = "SSW_FIELD_DIAGNOSTIC/2";
    internal const string Component = "VIEWER";
    internal const string Operation = "AGENT_CONNECTION_CHECK";

    private static readonly HashSet<string> AllowedModes =
    [
        "NORMAL",
        "SAME_PC"
    ];

    private static readonly HashSet<string> AllowedErrorCodes =
    [
        "NONE",
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
        "AGENT_TIMEOUT",
        "AGENT_UNREACHABLE",
        "AGENT_VERSION_MISMATCH",
        "LOCAL_AGENT_PREFLIGHT_FAILED",
        "LOCAL_AGENT_PREFLIGHT_TIMEOUT",
        "LOCAL_PRIVATE_IPV4_DISCOVERY_FAILED",
        "LOCAL_PRIVATE_IPV4_NOT_FOUND",
        "VIEWER_CONFIGURATION_INVALID",
        "VIEWER_CONNECTION_REQUIRED",
        "VIEWER_SETTINGS_WRITE_FAILED",
        "VIEWER_UNEXPECTED_ERROR"
    ];

    internal static ViewerFieldDiagnosticSnapshot Create(
        string mode,
        AgentConnectionProbeResult probeResult,
        int candidateCount,
        DateTimeOffset? generatedUtc = null,
        string? productVersion = null,
        string? windowsBuild = null,
        string? architecture = null)
    {
        ArgumentNullException.ThrowIfNull(probeResult);

        var safeMode = AllowedModes.Contains(mode) ? mode : "NORMAL";
        var succeeded = probeResult.Succeeded;
        var errorCode = succeeded
            ? "NONE"
            : AllowlistedErrorCode(probeResult.ErrorCode);
        var failedStage = succeeded
            ? "NONE"
            : StageName(probeResult.FailedStage);
        var stages = NormalizeStages(probeResult);
        var identity = probeResult.Identity;

        return new ViewerFieldDiagnosticSnapshot(
            generatedUtc ?? DateTimeOffset.UtcNow,
            SafeVersion(productVersion ?? AgentProductVersionPolicy.CurrentViewerVersion),
            SafeWindowsBuild(windowsBuild),
            SafeArchitecture(architecture),
            safeMode,
            Operation,
            succeeded ? "SUCCESS" : "FAILED",
            failedStage,
            errorCode,
            RecommendedAction(errorCode),
            Math.Clamp(candidateCount, 0, LocalAgentPreflight.DefaultMaxCandidateAttempts),
            SafeVersion(identity?.ProductVersion),
            SafeApiVersion(identity?.ApiVersion),
            stages);
    }

    internal static ViewerFieldDiagnosticSnapshot CreateApplyFailure(
        string mode,
        AgentConnectionProbeResult successfulProbeResult,
        int candidateCount,
        string? errorCode,
        DateTimeOffset? generatedUtc = null,
        string? productVersion = null,
        string? windowsBuild = null,
        string? architecture = null)
    {
        ArgumentNullException.ThrowIfNull(successfulProbeResult);

        var snapshot = Create(
            mode,
            successfulProbeResult,
            candidateCount,
            generatedUtc,
            productVersion,
            windowsBuild,
            architecture);
        var safeErrorCode = AllowlistedErrorCode(errorCode);
        return snapshot with
        {
            Result = "FAILED",
            FailedStage = ApplyFailureStage(safeErrorCode),
            ErrorCode = safeErrorCode,
            RecommendedActionCode = RecommendedAction(safeErrorCode)
        };
    }

    internal static string Format(ViewerFieldDiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var stages = NormalizeStages(snapshot.Stages, null);
        var builder = new StringBuilder(512);
        AppendLine(builder, Schema);
        Append(builder, "Component", Component);
        Append(builder, "ProductVersion", SafeVersion(snapshot.ProductVersion));
        Append(
            builder,
            "Environment",
            string.Join(
                '|',
                snapshot.GeneratedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                SafeWindowsBuild(snapshot.WindowsBuild),
                SafeArchitecture(snapshot.Architecture)));
        Append(
            builder,
            "Run",
            string.Join(
                '|',
                AllowedModes.Contains(snapshot.Mode) ? snapshot.Mode : "NORMAL",
                Operation,
                snapshot.Result == "SUCCESS" ? "SUCCESS" : "FAILED"));
        var failedStage = AllowedStageName(snapshot.FailedStage);
        Append(builder, "FailedStage", failedStage);
        var errorCode = AllowlistedErrorCode(snapshot.ErrorCode);
        Append(builder, "ErrorCode", errorCode);
        Append(builder, "Action", RecommendedAction(errorCode));
        Append(
            builder,
            "Stages",
            string.Join(
                '|',
                Enum.GetValues<AgentConnectionProbeStage>()
                    .Select(stage =>
                        CompactStageKey(stage) + ':' + CompactStateName(
                            stages.Single(item => item.Stage == stage),
                            failedStage))));
        Append(
            builder,
            "TimingMs",
            string.Join(
                '|',
                Enum.GetValues<AgentConnectionProbeStage>()
                    .Select(stage => ClampDuration(
                            stages.Single(item => item.Stage == stage).DurationMs)
                        .ToString(CultureInfo.InvariantCulture))));
        Append(
            builder,
            "Agent",
            string.Join(
                '|',
                Math.Clamp(snapshot.CandidateCount, 0, LocalAgentPreflight.DefaultMaxCandidateAttempts)
                    .ToString(CultureInfo.InvariantCulture),
                SafeVersion(snapshot.AgentProductVersion),
                SafeApiVersion(snapshot.ApiVersion)));
        return builder.ToString();
    }

    internal static string CreateSupportCode(
        ViewerFieldDiagnosticSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Result != "FAILED")
        {
            throw new ArgumentException(
                "SWD1 support codes are generated only for failed connection checks.",
                nameof(snapshot));
        }

        var stages = NormalizeStages(snapshot.Stages, null);
        string State(AgentConnectionProbeStage stage) =>
            StateName(stages.Single(item => item.Stage == stage).State);

        var errorCode = AllowlistedErrorCode(snapshot.ErrorCode);
        var payload = Swd1ViewerPayloadBuilder.Build(
            snapshot.ProductVersion,
            Operation,
            errorCode,
            errorCode,
            AllowedModes.Contains(snapshot.Mode) ? snapshot.Mode : "NORMAL",
            AllowedStageName(snapshot.FailedStage),
            State(AgentConnectionProbeStage.Address),
            State(AgentConnectionProbeStage.Dns),
            State(AgentConnectionProbeStage.Tcp),
            State(AgentConnectionProbeStage.Https),
            State(AgentConnectionProbeStage.Identity),
            Math.Clamp(
                snapshot.CandidateCount,
                0,
                LocalAgentPreflight.DefaultMaxCandidateAttempts),
            snapshot.AgentProductVersion,
            snapshot.ApiVersion);
        return Swd1SupportCode.Encode(payload);
    }

    private static IReadOnlyList<AgentConnectionProbeStageSnapshot> NormalizeStages(
        AgentConnectionProbeResult result)
    {
        var failedStage = result.Succeeded ? null : result.FailedStage;
        return NormalizeStages(result.StageSnapshots, failedStage);
    }

    private static IReadOnlyList<AgentConnectionProbeStageSnapshot> NormalizeStages(
        IReadOnlyList<AgentConnectionProbeStageSnapshot>? stages,
        AgentConnectionProbeStage? failedStage)
    {
        var lookup = (stages ?? [])
            .Where(item => Enum.IsDefined(item.Stage))
            .GroupBy(item => item.Stage)
            .ToDictionary(group => group.Key, group => group.Last());

        return Enum.GetValues<AgentConnectionProbeStage>()
            .Select(stage =>
            {
                if (lookup.TryGetValue(stage, out var snapshot))
                {
                    return new AgentConnectionProbeStageSnapshot(
                        stage,
                        AllowedState(snapshot.State),
                        ClampDuration(snapshot.DurationMs));
                }

                return new AgentConnectionProbeStageSnapshot(
                    stage,
                    stage == failedStage
                        ? AgentConnectionProbeState.Failed
                        : AgentConnectionProbeState.Pending,
                    0);
            })
            .ToArray();
    }

    private static void Append(StringBuilder builder, string key, string value) =>
        AppendLine(builder, key + '=' + value);

    private static void AppendLine(StringBuilder builder, string value) =>
        builder.Append(value).Append("\r\n");

    private static string SafeVersion(string? value)
    {
        var normalized = AgentProductVersionPolicy.Normalize(value);
        return normalized.Length is > 0 and <= 64
               && IsDiagnosticProductVersion(normalized)
            ? normalized
            : "UNKNOWN";
    }

    private static bool IsDiagnosticProductVersion(string value)
    {
        var prereleaseSeparator = value.IndexOf('-');
        var release = prereleaseSeparator < 0
            ? value
            : value[..prereleaseSeparator];
        var releaseParts = release.Split('.');
        if (releaseParts.Length != 3
            || releaseParts.Any(part =>
                part.Length is < 1 or > 10
                || !part.All(character => character is >= '0' and <= '9')))
        {
            return false;
        }

        if (prereleaseSeparator < 0)
        {
            return true;
        }

        var prerelease = value[(prereleaseSeparator + 1)..];
        return prerelease.Length > 0
               && prerelease.Split('.').All(identifier =>
                   identifier.Length > 0
                   && identifier.All(character =>
                       character is >= 'A' and <= 'Z'
                           or >= 'a' and <= 'z'
                           or >= '0' and <= '9'
                           or '-'));
    }

    private static string SafeWindowsBuild(string? value)
    {
        var candidate = value;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            candidate = Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture);
        }

        return int.TryParse(candidate, NumberStyles.None, CultureInfo.InvariantCulture, out var build)
               && build is >= 0 and <= 999_999
            ? build.ToString(CultureInfo.InvariantCulture)
            : "UNKNOWN";
    }

    private static string SafeArchitecture(string? value)
    {
        var candidate = string.IsNullOrWhiteSpace(value)
            ? RuntimeInformation.OSArchitecture.ToString()
            : value;
        return candidate.ToUpperInvariant() switch
        {
            "X86" => "X86",
            "X64" => "X64",
            "ARM" => "ARM",
            "ARM64" => "ARM64",
            _ => "UNKNOWN"
        };
    }

    private static string SafeApiVersion(int? value) =>
        value is >= 0 and <= 9_999
            ? value.Value.ToString(CultureInfo.InvariantCulture)
            : "UNKNOWN";

    private static string SafeApiVersion(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var apiVersion)
            ? SafeApiVersion(apiVersion)
            : "UNKNOWN";

    private static string AllowlistedErrorCode(string? value) =>
        value is not null && AllowedErrorCodes.Contains(value)
            ? value
            : "VIEWER_UNEXPECTED_ERROR";

    private static string RecommendedAction(string errorCode) => errorCode switch
    {
        "NONE" => "NONE",
        "AGENT_DNS_FAILED" => "CHECK_AGENT_ADDRESS_DNS",
        "AGENT_CONNECTION_REFUSED" or "AGENT_NOT_READY" or "LOCAL_AGENT_PREFLIGHT_FAILED" =>
            "CHECK_AGENT_SERVICE",
        "AGENT_TIMEOUT" or "AGENT_UNREACHABLE" or "LOCAL_AGENT_PREFLIGHT_TIMEOUT" =>
            "CHECK_NETWORK_FIREWALL",
        "AGENT_ACCESS_DENIED" or "AGENT_CLIENT_NOT_ALLOWED" =>
            "CHECK_ALLOWED_VIEWER_IP",
        "AGENT_PROTOCOL_MISMATCH" or "AGENT_VERSION_MISMATCH" or "AGENT_RESPONSE_INVALID" =>
            "USE_MATCHING_RELEASE",
        "AGENT_IDENTITY_CHANGED" => "VERIFY_AGENT_REPLACEMENT",
        "LOCAL_PRIVATE_IPV4_DISCOVERY_FAILED" or "LOCAL_PRIVATE_IPV4_NOT_FOUND" =>
            "CHECK_LOCAL_NETWORK_ADAPTER",
        "VIEWER_SETTINGS_WRITE_FAILED" => "CHECK_VIEWER_STORAGE",
        "VIEWER_CONFIGURATION_INVALID" or "VIEWER_CONNECTION_REQUIRED" =>
            "CHECK_VIEWER_CONNECTION_SETTINGS",
        _ => "CHECK_AGENT_DIAGNOSTIC"
    };

    private static string ApplyFailureStage(string errorCode) => errorCode switch
    {
        "AGENT_IDENTITY_CHANGED" or "AGENT_PROTOCOL_MISMATCH" => "HTTPS",
        "AGENT_DNS_FAILED" => "DNS",
        "AGENT_CONNECTION_REFUSED" or "AGENT_TIMEOUT" or "AGENT_UNREACHABLE" => "TCP",
        "VIEWER_SETTINGS_WRITE_FAILED" => "SETTINGS",
        _ => "IDENTITY"
    };

    private static string AllowedStageName(string? value) => value switch
    {
        "NONE" => "NONE",
        "ADDRESS" => "ADDRESS",
        "DNS" => "DNS",
        "TCP" => "TCP",
        "HTTPS" => "HTTPS",
        "IDENTITY" => "IDENTITY",
        "SETTINGS" => "SETTINGS",
        _ => "UNKNOWN"
    };

    private static string StageName(AgentConnectionProbeStage? stage) => stage switch
    {
        AgentConnectionProbeStage.Address => "ADDRESS",
        AgentConnectionProbeStage.Dns => "DNS",
        AgentConnectionProbeStage.Tcp => "TCP",
        AgentConnectionProbeStage.Https => "HTTPS",
        AgentConnectionProbeStage.Identity => "IDENTITY",
        _ => "UNKNOWN"
    };

    private static string CompactStageKey(AgentConnectionProbeStage stage) => stage switch
    {
        AgentConnectionProbeStage.Address => "ADDR",
        AgentConnectionProbeStage.Dns => "DNS",
        AgentConnectionProbeStage.Tcp => "TCP",
        AgentConnectionProbeStage.Https => "HTTPS",
        AgentConnectionProbeStage.Identity => "ID",
        _ => "UNKNOWN"
    };

    private static string CompactStateName(
        AgentConnectionProbeStageSnapshot stage,
        string failedStage)
    {
        if (stage.State == AgentConnectionProbeState.Succeeded)
        {
            return "OK";
        }

        if (stage.State == AgentConnectionProbeState.Failed)
        {
            return "FAIL";
        }

        if (TryParseProbeStage(failedStage, out var failedProbeStage)
            && stage.Stage > failedProbeStage)
        {
            return "SKIP";
        }

        return "PENDING";
    }

    private static bool TryParseProbeStage(
        string stageName,
        out AgentConnectionProbeStage stage)
    {
        stage = stageName switch
        {
            "ADDRESS" => AgentConnectionProbeStage.Address,
            "DNS" => AgentConnectionProbeStage.Dns,
            "TCP" => AgentConnectionProbeStage.Tcp,
            "HTTPS" => AgentConnectionProbeStage.Https,
            "IDENTITY" => AgentConnectionProbeStage.Identity,
            _ => default
        };
        return stageName is "ADDRESS" or "DNS" or "TCP" or "HTTPS" or "IDENTITY";
    }

    private static AgentConnectionProbeState AllowedState(AgentConnectionProbeState state) =>
        Enum.IsDefined(state) ? state : AgentConnectionProbeState.Pending;

    private static string StateName(AgentConnectionProbeState state) => state switch
    {
        AgentConnectionProbeState.Pending => "NOT_RUN",
        AgentConnectionProbeState.Running => "RUNNING",
        AgentConnectionProbeState.Succeeded => "SUCCEEDED",
        AgentConnectionProbeState.Failed => "FAILED",
        _ => "NOT_RUN"
    };

    private static long ClampDuration(long value) =>
        Math.Clamp(value, 0, AgentConnectionProbeTimingProgress.MaximumDurationMs);
}

internal sealed record ViewerFieldDiagnosticWriteResult(bool Succeeded, string ErrorCode);

internal sealed class ViewerFieldDiagnosticWriter
{
    private static readonly UTF8Encoding Utf8WithBom = new(true);

    public async Task<ViewerFieldDiagnosticWriteResult> WriteAsync(
        string path,
        ViewerFieldDiagnosticSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolvePath(path, out var fullPath))
        {
            return new ViewerFieldDiagnosticWriteResult(false, "DIAGNOSTIC_WRITE_FAILED");
        }

        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var content = ViewerFieldDiagnostic.Format(snapshot);
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             8 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream, Utf8WithBom))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            File.Move(temporaryPath, fullPath, true);
            return new ViewerFieldDiagnosticWriteResult(true, "DIAGNOSTIC_WRITE_OK");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ViewerFieldDiagnosticWriteResult(false, "DIAGNOSTIC_WRITE_FAILED");
        }
        catch
        {
            return new ViewerFieldDiagnosticWriteResult(false, "DIAGNOSTIC_WRITE_FAILED");
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Best-effort cleanup must not replace the stable write result.
            }
        }
    }

    private static bool TryResolvePath(string? path, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32_000)
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            return Path.IsPathFullyQualified(fullPath)
                   && string.Equals(Path.GetExtension(fullPath), ".txt", StringComparison.OrdinalIgnoreCase)
                   && !string.IsNullOrWhiteSpace(directory)
                   && Directory.Exists(directory);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
