using System.Globalization;
using System.Text;
using SamsungSwitchWatch.Agent.Setup.Deployment;
using SamsungSwitchWatch.Support;

namespace SamsungSwitchWatch.Agent.Setup;

internal sealed record SetupRecoveryActionState(
    bool InstallEnabled,
    bool RecoverVisible,
    bool RecoverEnabled);

internal static class SetupRecoveryActionPolicy
{
    public static SetupRecoveryActionState Evaluate(
        bool diagnosticsOnly,
        bool busy,
        PendingRecoveryInspection recovery) =>
        new(
            InstallEnabled:
                !busy &&
                !diagnosticsOnly &&
                !recovery.Exists,
            RecoverVisible:
                recovery.Exists &&
                recovery.CanRecover,
            RecoverEnabled:
                !busy &&
                !diagnosticsOnly &&
                recovery.Exists &&
                recovery.CanRecover);
}

internal enum SetupRecoveryCompletionSeverity
{
    Success,
    Warning,
    Error
}

internal sealed record SetupRecoveryCompletionState(
    bool ReadyForInstall,
    bool UseInspectionResult,
    SetupRecoveryCompletionSeverity Severity,
    string StatusText,
    string GuidanceText);

internal static class SetupRecoveryCompletionPolicy
{
    public static SetupRecoveryCompletionState Evaluate(
        SetupOperationResult recoveryResult,
        PendingRecoveryInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(recoveryResult);
        ArgumentNullException.ThrowIfNull(inspection);

        if (recoveryResult.Succeeded && !inspection.Exists)
        {
            return new SetupRecoveryCompletionState(
                ReadyForInstall: true,
                UseInspectionResult: false,
                SetupRecoveryCompletionSeverity.Success,
                "복구 완료 · 설치 준비됨",
                "복구가 완료되었습니다. 설치 / 업데이트 버튼을 눌러 다음 작업을 시작하세요.");
        }

        if (recoveryResult.Succeeded)
        {
            return inspection.CanRecover
                ? new SetupRecoveryCompletionState(
                    ReadyForInstall: false,
                    UseInspectionResult: true,
                    SetupRecoveryCompletionSeverity.Warning,
                    $"복구 후 상태 재확인 필요 · {inspection.Code}",
                    "이전 작업 기록이 아직 남아 있습니다. 이전 상태 복구를 다시 실행한 뒤 완료 여부를 확인하세요.")
                : new SetupRecoveryCompletionState(
                    ReadyForInstall: false,
                    UseInspectionResult: true,
                    SetupRecoveryCompletionSeverity.Error,
                    $"복구 후 상태 확인 실패 · {inspection.Code}",
                    "복구 상태를 안전하게 확인할 수 없습니다. 설치를 진행하지 말고 익명 진단을 관리자에게 전달하세요.");
        }

        return new SetupRecoveryCompletionState(
            ReadyForInstall: false,
            UseInspectionResult: false,
            SetupRecoveryCompletionSeverity.Error,
            $"복구 실패 · {recoveryResult.Code}",
            "설치는 계속 잠겨 있습니다. 아래 원래 실패 원인과 복구 실패 단계를 확인하세요.");
    }
}

internal static class SetupResultPresentation
{
    public static SetupOperationResult BuildPendingRecoveryResult(
        PendingRecoveryInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        var state = inspection.CanRecover
            ? SetupStepState.Warning
            : SetupStepState.Failed;

        return SetupOperationResult.Failure(
            inspection.Code,
            inspection.Message,
            [
                new SetupStepResult(
                    inspection.Code,
                    inspection.CanRecover
                        ? "이전 상태 복구 필요"
                        : "관리자 확인 필요",
                    state,
                    inspection.Message)
            ]) with
        {
            PrimaryFailureCode = inspection.PrimaryFailureCode,
            PrimaryFailureMessage = inspection.PrimaryFailureMessage,
            RollbackFailureCodes = inspection.FailureCodes,
            AgentHealthCode = inspection.AgentHealthCode,
            AgentRestartObserved = inspection.AgentRestartObserved,
            AgentServiceRunningObserved =
                inspection.AgentServiceRunningObserved,
            AgentListenerOwnedObserved =
                inspection.AgentListenerOwnedObserved,
            AgentHttpAttemptCount = inspection.AgentHttpAttemptCount,
            AgentLastTransportPhase = inspection.AgentLastTransportPhase
        };
    }

    public static IReadOnlyList<SetupStepResult> BuildSteps(
        SetupOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var primaryCode = NormalizeCode(result.PrimaryFailureCode);
        var allRollbackCodes = result.RollbackFailureCodes
            .Select(NormalizeCode)
            .Where(code => code is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var hasTargetSpecificCleanupFailure =
            allRollbackCodes.Any(IsTargetSpecificCleanupFailure);
        var rollbackCodes = allRollbackCodes
            .Where(code =>
                !hasTargetSpecificCleanupFailure ||
                !string.Equals(
                    code,
                    SetupErrorCodes.RollbackEvidenceCleanupFailed,
                    StringComparison.Ordinal))
            .ToArray();
        var rollbackCodeSet =
            allRollbackCodes.ToHashSet(StringComparer.Ordinal);

        var steps = result.Steps
            .Where(step =>
                !string.Equals(
                    step.Code,
                    SetupErrorCodes.RollbackFailed,
                    StringComparison.Ordinal) ||
                rollbackCodes.Length == 0)
            .Where(step =>
                primaryCode is null ||
                !string.Equals(step.Code, primaryCode, StringComparison.Ordinal))
            .Where(step => !rollbackCodeSet.Contains(step.Code))
            .DistinctBy(
                step => (step.Code, step.Label, step.State, step.Message))
            .ToList();

        if (primaryCode is not null)
        {
            steps.Add(new SetupStepResult(
                primaryCode,
                "원래 설치 실패",
                SetupStepState.Failed,
                string.IsNullOrWhiteSpace(result.PrimaryFailureMessage)
                    ? "설치 작업이 실패했습니다. 아래 코드와 이전 상태 복구 결과를 함께 확인하세요."
                    : result.PrimaryFailureMessage));
        }

        steps.AddRange(rollbackCodes.Select(code =>
            new SetupStepResult(
                code,
                RollbackFailureLabel(code),
                SetupStepState.Failed,
                RollbackFailureMessage(code))));

        return steps;
    }

    private static string? NormalizeCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return code.Trim();
    }

    private static string RollbackFailureMessage(string code) =>
        code switch
        {
            SetupErrorCodes.RollbackStateMismatch =>
                "복구 대상 상태가 기록과 달라 이전 상태 복구를 중단했습니다.",
            SetupErrorCodes.RollbackServiceStopFailed =>
                "기존 Agent 서비스를 안전하게 중지하지 못했습니다.",
            SetupErrorCodes.RollbackFileRestoreFailed =>
                "기존 Agent 실행 파일을 완전히 복원하지 못했습니다.",
            SetupErrorCodes.RollbackDataCleanupFailed =>
                "설치 중 생성된 데이터 정리를 완료하지 못했습니다.",
            SetupErrorCodes.RollbackServiceRestoreFailed =>
                "기존 Agent 서비스 상태를 복원하지 못했습니다.",
            SetupErrorCodes.RollbackHttpsFirewallRestoreFailed =>
                "Agent HTTPS 방화벽 규칙을 이전 상태로 복원하지 못했습니다.",
            SetupErrorCodes.RollbackLegacyFirewallRestoreFailed =>
                "이전 버전 방화벽 규칙을 복원하지 못했습니다.",
            SetupErrorCodes.RollbackJournalWriteFailed =>
                "복구 완료 상태를 안전하게 기록하지 못했습니다.",
            SetupErrorCodes.RollbackEvidenceCleanupFailed =>
                "복구 확인 후 남은 설치 흔적을 정리하지 못했습니다.",
            SetupErrorCodes.RollbackStagingCleanupFailed =>
                "임시 설치 자료 정리를 완료하지 못했습니다.",
            SetupErrorCodes.RollbackBackupCleanupFailed =>
                "이전 파일 백업 자료 정리를 완료하지 못했습니다.",
            SetupErrorCodes.RollbackFailedDirectoryCleanupFailed =>
                "실패한 설치 자료 정리를 완료하지 못했습니다.",
            SetupErrorCodes.RollbackJournalCleanupFailed =>
                "복구 작업 기록을 삭제하고 결과를 확인하지 못했습니다.",
            _ => "이전 상태 복구의 일부 단계를 완료하지 못했습니다."
        };

    private static string RollbackFailureLabel(string code) =>
        code switch
        {
            SetupErrorCodes.RollbackStagingCleanupFailed =>
                "임시 설치 자료 정리",
            SetupErrorCodes.RollbackBackupCleanupFailed =>
                "이전 파일 정리",
            SetupErrorCodes.RollbackFailedDirectoryCleanupFailed =>
                "실패 설치 자료 정리",
            SetupErrorCodes.RollbackJournalCleanupFailed =>
                "복구 기록 정리",
            _ => "이전 상태 복구 실패"
        };

    private static bool IsTargetSpecificCleanupFailure(string code) =>
        code is
            SetupErrorCodes.RollbackStagingCleanupFailed or
            SetupErrorCodes.RollbackBackupCleanupFailed or
            SetupErrorCodes.RollbackFailedDirectoryCleanupFailed or
            SetupErrorCodes.RollbackJournalCleanupFailed;
}

internal sealed record SetupFailureDiagnosticContext(
    string ProductVersion,
    DateTimeOffset UtcTimestamp,
    string Operation,
    SetupOperationResult Result,
    PendingRecoveryInspection Recovery);

internal readonly record struct SetupFailureDiagnosticProjection(
    string Stage,
    string Category,
    long DurationMilliseconds)
{
    public static SetupFailureDiagnosticProjection Create(
        SetupOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var resultStage = StageForResultCode(result.Code);
        if (resultStage == "RECOVERY")
        {
            return new SetupFailureDiagnosticProjection(
                resultStage,
                result.Code == SetupErrorCodes.Unexpected
                    ? "UNKNOWN"
                    : "CLASSIFIED",
                -1);
        }

        if (result.DiagnosticMetadata?.Failure is { } failure)
        {
            return new SetupFailureDiagnosticProjection(
                StageToken(failure.Stage),
                CategoryToken(failure.Category),
                failure.DurationMilliseconds);
        }

        return new SetupFailureDiagnosticProjection(
            resultStage,
            result.Code == SetupErrorCodes.Unexpected
                ? "UNKNOWN"
                : "CLASSIFIED",
            -1);
    }

    private static string StageToken(SetupFailureStage stage) =>
        stage switch
        {
            SetupFailureStage.OperationLock => "OPERATION_LOCK",
            SetupFailureStage.Administrator => "ADMINISTRATOR",
            SetupFailureStage.RecoveryJournal => "RECOVERY_JOURNAL",
            SetupFailureStage.Input => "INPUT",
            SetupFailureStage.PackageValidation => "PACKAGE_VALIDATION",
            SetupFailureStage.FileSystem => "FILESYSTEM",
            SetupFailureStage.Configuration => "CONFIGURATION",
            SetupFailureStage.FileStaging => "FILE_STAGING",
            SetupFailureStage.ServiceStop => "SERVICE_STOP",
            SetupFailureStage.FileActivation => "FILE_ACTIVATION",
            SetupFailureStage.ServiceConfiguration => "SERVICE_CONFIGURATION",
            SetupFailureStage.Firewall => "FIREWALL",
            SetupFailureStage.ServiceStart => "SERVICE_START",
            SetupFailureStage.Readiness => "READINESS",
            SetupFailureStage.CommitCleanup => "COMMIT_CLEANUP",
            SetupFailureStage.Recovery => "RECOVERY",
            SetupFailureStage.UiOperation => "UI_OPERATION",
            _ => "UNKNOWN"
        };

    private static string CategoryToken(SetupFailureCategory category) =>
        category switch
        {
            SetupFailureCategory.AccessDenied => "ACCESS_DENIED",
            SetupFailureCategory.Io => "IO",
            SetupFailureCategory.Timeout => "TIMEOUT",
            SetupFailureCategory.WindowsApi => "WINDOWS_API",
            SetupFailureCategory.InvalidState => "INVALID_STATE",
            SetupFailureCategory.Platform => "PLATFORM",
            _ => "UNKNOWN"
        };

    private static string StageForResultCode(string code) =>
        code switch
        {
            SetupErrorCodes.PackageNotFound or
            SetupErrorCodes.ManifestInvalid or
            SetupErrorCodes.PackageHashMismatch => "PACKAGE_VALIDATION",
            SetupErrorCodes.ViewerIpInvalid or
            SetupErrorCodes.NetworkSelectionInvalid or
            SetupErrorCodes.ExistingNetworksNotLoaded => "INPUT",
            SetupErrorCodes.AdministratorRequired => "ADMINISTRATOR",
            SetupErrorCodes.PathInvalid or
            SetupErrorCodes.PathUntrusted or
            SetupErrorCodes.PathNotWritable => "FILESYSTEM",
            SetupErrorCodes.ConfigurationInvalid => "CONFIGURATION",
            SetupErrorCodes.ServiceFailed => "SERVICE",
            SetupErrorCodes.FirewallFailed => "FIREWALL",
            SetupErrorCodes.HealthFailed => "READINESS",
            SetupErrorCodes.RecoveryRequired or
            SetupErrorCodes.RollbackStateMismatch or
            SetupErrorCodes.RollbackJournalWriteFailed => "RECOVERY_JOURNAL",
            SetupErrorCodes.RollbackFailed or
            SetupErrorCodes.RollbackServiceStopFailed or
            SetupErrorCodes.RollbackFileRestoreFailed or
            SetupErrorCodes.RollbackDataCleanupFailed or
            SetupErrorCodes.RollbackServiceRestoreFailed or
            SetupErrorCodes.RollbackHttpsFirewallRestoreFailed or
            SetupErrorCodes.RollbackLegacyFirewallRestoreFailed or
            SetupErrorCodes.RollbackEvidenceCleanupFailed or
            SetupErrorCodes.RollbackStagingCleanupFailed or
            SetupErrorCodes.RollbackBackupCleanupFailed or
            SetupErrorCodes.RollbackFailedDirectoryCleanupFailed or
            SetupErrorCodes.RollbackJournalCleanupFailed => "RECOVERY",
            SetupErrorCodes.AlreadyRunning => "OPERATION_LOCK",
            SetupErrorCodes.Cancelled => "CANCELLED",
            _ => "UNKNOWN"
        };
}

internal static class SetupFailureDiagnosticFormatter
{
    public static string Format(SetupFailureDiagnosticContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Result.Succeeded)
        {
            throw new ArgumentException(
                "Failure diagnostics require a failed operation result.",
                nameof(context));
        }

        var rollbackCodes = context.Result.RollbackFailureCodes
            .Concat(context.Recovery.FailureCodes)
            .Select(SafeToken)
            .Where(value => value != "none")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var failure = SetupFailureDiagnosticProjection.Create(context.Result);

        var lines = new[]
        {
            "Samsung Switch Watch Agent Setup 진단정보",
            $"ProductVersion={SafeToken(context.ProductVersion)}",
            $"UtcTimestamp={context.UtcTimestamp.UtcDateTime:O}",
            $"Operation={SafeToken(context.Operation)}",
            $"ResultCode={SafeToken(context.Result.Code)}",
            $"PrimaryFailureCode={SafeToken(
                context.Result.PrimaryFailureCode ??
                context.Recovery.PrimaryFailureCode)}",
            $"FailedStage={failure.Stage}",
            $"FailureCategory={failure.Category}",
            $"FailureStageDurationMs={
                MillisecondsToken(failure.DurationMilliseconds)}",
            $"AgentHealthCode={SafeAgentHealthToken(
                context.Result.AgentHealthCode ??
                context.Recovery.AgentHealthCode)}",
            $"AgentRestartObserved={
                BooleanToken(
                    context.Result.AgentRestartObserved ||
                    context.Recovery.AgentRestartObserved)}",
            $"ServiceRunningObserved={
                BooleanToken(
                    context.Result.AgentServiceRunningObserved ||
                    context.Recovery.AgentServiceRunningObserved)}",
            $"ListenerOwnedObserved={
                BooleanToken(
                    context.Result.AgentListenerOwnedObserved ||
                    context.Recovery.AgentListenerOwnedObserved)}",
            $"HttpAttemptCount={
                AttemptCountToken(
                    Math.Max(
                        context.Result.AgentHttpAttemptCount,
                        context.Recovery.AgentHttpAttemptCount))}",
            $"LastTransportPhase={
                TransportPhaseToken(
                    LastTransportPhase(
                        context.Result,
                        context.Recovery))}",
            $"RollbackFailureCodes={
                (rollbackCodes.Length == 0 ? "none" : string.Join(",", rollbackCodes))}",
            $"RecoveryJournalExists={
                context.Recovery.Exists.ToString().ToLowerInvariant()}",
            $"RecoveryCanRun={
                context.Recovery.CanRecover.ToString().ToLowerInvariant()}",
            $"JournalFormatVersion={
                context.Recovery.JournalFormatVersion?.ToString(
                    CultureInfo.InvariantCulture) ?? "none"}",
            $"JournalStage={SafeToken(context.Recovery.JournalStage)}",
            $"ServiceState={SafeToken(context.Recovery.ServiceState)}",
            $"InstallDirectoryExists={
                EvidenceToken(
                    context.Recovery,
                    context.Recovery.InstallDirectoryExists)}",
            $"StagingDirectoryExists={
                EvidenceToken(
                    context.Recovery,
                    context.Recovery.StagingDirectoryExists)}",
            $"BackupDirectoryExists={
                EvidenceToken(
                    context.Recovery,
                    context.Recovery.BackupDirectoryExists)}",
            $"FailedDirectoryExists={
                EvidenceToken(
                    context.Recovery,
                    context.Recovery.FailedDirectoryExists)}",
            $"DataDirectoryExists={
                EvidenceToken(
                    context.Recovery,
                    context.Recovery.DataDirectoryExists)}"
        };

        return string.Join(Environment.NewLine, lines);
    }

    private static string SafeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "none";
        }

        var builder = new StringBuilder(capacity: Math.Min(value.Length, 64));
        foreach (var character in value.Trim())
        {
            if (builder.Length == 64)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-')
            {
                builder.Append(character);
            }
            else
            {
                return "unavailable";
            }
        }

        return builder.Length == 0 ? "none" : builder.ToString();
    }

    private static string SafeAgentHealthToken(string? value)
    {
        if (!Enum.TryParse<AgentHealthProbeCode>(
                value,
                ignoreCase: false,
                out var code))
        {
            return SafeToken(value);
        }

        return code switch
        {
            AgentHealthProbeCode.HttpsTlsFailed => "HTTPS_TLS_FAILED",
            AgentHealthProbeCode.HttpsRequestTimeout =>
                "HTTPS_REQUEST_TIMEOUT",
            AgentHealthProbeCode.HttpsConnectionReset =>
                "HTTPS_CONNECTION_RESET",
            AgentHealthProbeCode.HttpsEof => "HTTPS_EOF",
            AgentHealthProbeCode.HttpsConnectFailed =>
                "HTTPS_CONNECT_FAILED",
            _ => SafeToken(value)
        };
    }

    private static string BooleanToken(bool value) =>
        value ? "true" : "false";

    private static string MillisecondsToken(long milliseconds) =>
        milliseconds >= 0
            ? Math.Min(86_400_000, milliseconds)
                .ToString(CultureInfo.InvariantCulture)
            : "unknown";

    private static string AttemptCountToken(int count) =>
        Math.Clamp(count, 0, 10_000).ToString(CultureInfo.InvariantCulture);

    private static string TransportPhaseToken(
        AgentHealthTransportPhase phase) =>
        phase switch
        {
            AgentHealthTransportPhase.NotStarted => "NOT_STARTED",
            AgentHealthTransportPhase.ListenerOwned => "LISTENER_OWNED",
            AgentHealthTransportPhase.RequestStarted => "REQUEST_STARTED",
            AgentHealthTransportPhase.ResponseHeaders => "RESPONSE_HEADERS",
            AgentHealthTransportPhase.ResponseBody => "RESPONSE_BODY",
            AgentHealthTransportPhase.ReadinessValidated =>
                "READINESS_VALIDATED",
            _ => "NOT_STARTED"
        };

    private static AgentHealthTransportPhase LastTransportPhase(
        SetupOperationResult result,
        PendingRecoveryInspection recovery) =>
        result.AgentLastTransportPhase != AgentHealthTransportPhase.NotStarted
            ? result.AgentLastTransportPhase
            : recovery.AgentLastTransportPhase;

    private static string EvidenceToken(
        PendingRecoveryInspection recovery,
        bool value) =>
        recovery.EvidenceStateKnown
            ? BooleanToken(value)
            : "unknown";
}

internal sealed record SetupFieldDiagnosticContext(
    string ProductVersion,
    DateTimeOffset GeneratedUtc,
    string WindowsBuild,
    string Architecture,
    string Operation,
    TimeSpan OperationDuration,
    SetupOperationResult Result,
    PendingRecoveryInspection Recovery);

internal static class SetupFieldDiagnosticFormatter
{
    private const string Unavailable = "UNAVAILABLE";
    private const string NotRun = "NOT_RUN";

    private static readonly HashSet<string> AllowedErrorCodes =
        new(StringComparer.Ordinal)
        {
            SetupErrorCodes.Ok,
            SetupErrorCodes.PackageNotFound,
            SetupErrorCodes.ManifestInvalid,
            SetupErrorCodes.PackageHashMismatch,
            SetupErrorCodes.ViewerIpInvalid,
            SetupErrorCodes.NetworkSelectionInvalid,
            SetupErrorCodes.ExistingNetworksNotLoaded,
            SetupErrorCodes.AdministratorRequired,
            SetupErrorCodes.PathInvalid,
            SetupErrorCodes.PathUntrusted,
            SetupErrorCodes.PathNotWritable,
            SetupErrorCodes.ConfigurationInvalid,
            SetupErrorCodes.ServiceFailed,
            SetupErrorCodes.FirewallFailed,
            SetupErrorCodes.HealthFailed,
            SetupErrorCodes.RollbackFailed,
            SetupErrorCodes.RecoveryRequired,
            SetupErrorCodes.RollbackStateMismatch,
            SetupErrorCodes.RollbackServiceStopFailed,
            SetupErrorCodes.RollbackFileRestoreFailed,
            SetupErrorCodes.RollbackDataCleanupFailed,
            SetupErrorCodes.RollbackServiceRestoreFailed,
            SetupErrorCodes.RollbackHttpsFirewallRestoreFailed,
            SetupErrorCodes.RollbackLegacyFirewallRestoreFailed,
            SetupErrorCodes.RollbackJournalWriteFailed,
            SetupErrorCodes.RollbackEvidenceCleanupFailed,
            SetupErrorCodes.RollbackStagingCleanupFailed,
            SetupErrorCodes.RollbackBackupCleanupFailed,
            SetupErrorCodes.RollbackFailedDirectoryCleanupFailed,
            SetupErrorCodes.RollbackJournalCleanupFailed,
            SetupErrorCodes.AlreadyRunning,
            SetupErrorCodes.Cancelled,
            SetupErrorCodes.Unexpected,
            SetupErrorCodes.DiagnosticWriteFailed
        };

    private static readonly HashSet<string> AllowedStageCodes =
        new(AllowedErrorCodes, StringComparer.Ordinal)
        {
            "ADMINISTRATOR_OK",
            "INPUT_VALID",
            "PACKAGE_VALID",
            "PATHS_READY",
            "FIREWALL_OVERLAP_PROTECTED",
            "FIREWALL_GATE_READY",
            "SERVICE_FOUND",
            "SERVICE_NOT_INSTALLED",
            "FIREWALL_EXACT",
            "FIREWALL_UPDATE_REQUIRED",
            "FIREWALL_NOT_INSTALLED",
            "PACKAGE_STAGED",
            "SERVICE_CONFIGURED",
            "FIREWALL_CONFIGURED",
            "SERVICE_STARTED",
            "AGENT_READY",
            "AGENT_NOT_READY",
            "BACKUP_CLEANUP_PENDING",
            "JOURNAL_CLEANUP_PENDING",
            "RECOVERY_NOT_REQUIRED",
            "ROLLBACK_COMPLETED",
            "ROLLBACK_RECOVERY_CLEANED",
            "COMMITTED_TRANSACTION_CLEANED"
        };

    private static readonly HashSet<string> AllowedFirewallDecisionCodes =
        new(StringComparer.Ordinal)
        {
            "FIREWALL_OVERLAP_PROTECTED",
            "FIREWALL_GATE_READY",
            "FIREWALL_EXACT",
            "FIREWALL_UPDATE_REQUIRED",
            "FIREWALL_NOT_INSTALLED",
            "FIREWALL_CONFIGURED",
            SetupErrorCodes.FirewallFailed,
            SetupErrorCodes.RollbackHttpsFirewallRestoreFailed,
            SetupErrorCodes.RollbackLegacyFirewallRestoreFailed,
            FirewallRuleMismatchCodes.Missing,
            FirewallRuleMismatchCodes.Disabled,
            FirewallRuleMismatchCodes.Direction,
            FirewallRuleMismatchCodes.Action,
            FirewallRuleMismatchCodes.Protocol,
            FirewallRuleMismatchCodes.LocalPort,
            FirewallRuleMismatchCodes.RemoteAddress,
            FirewallRuleMismatchCodes.Profiles,
            FirewallRuleMismatchCodes.EdgeTraversal
        };

    public static string Format(SetupFieldDiagnosticContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Result);
        ArgumentNullException.ThrowIfNull(context.Recovery);

        var operation = context.Operation switch
        {
            "preflight" => "PREFLIGHT",
            "install" => "INSTALL",
            "recovery" => "RECOVERY",
            _ => Unavailable
        };
        var resultCode = AllowedCode(context.Result.Code, AllowedErrorCodes);
        var primaryCode = AllowedCode(
            context.Result.PrimaryFailureCode,
            AllowedErrorCodes,
            resultCode);
        var failure = SetupFailureDiagnosticProjection.Create(context.Result);
        var stages = BuildStages(context.Result);
        var firewallDecisionCodes = BuildFirewallDecisionCodes(
            context.Result,
            stages);

        var lines = new List<string>
        {
            "SSW_FIELD_DIAGNOSTIC/1",
            "Component=AGENT_SETUP",
            $"ProductVersion={SafeToken(context.ProductVersion)}",
            $"GeneratedUtc={context.GeneratedUtc.UtcDateTime:yyyyMMddTHHmmssfffZ}",
            $"WindowsBuild={WindowsBuildToken(context.WindowsBuild)}",
            $"Architecture={ArchitectureToken(context.Architecture)}",
            $"Operation={operation}",
            $"Result={(context.Result.Succeeded ? "SUCCESS" : "FAILURE")}",
            $"FailedStage={
                (context.Result.Succeeded ? "NONE" : failure.Stage)}",
            $"ErrorCode={
                (context.Result.Succeeded ? SetupErrorCodes.Ok : resultCode)}",
            $"PrimaryFailureCode={
                (context.Result.Succeeded ? "NONE" : primaryCode)}",
            $"FailureCategory={
                (context.Result.Succeeded ? NotRun : failure.Category)}",
            $"FailureStageDurationMs={
                (context.Result.Succeeded
                    ? "unknown"
                    : MillisecondsToken(failure.DurationMilliseconds))}",
            $"RecommendedActionCode={
                RecommendedActionCode(
                    context.Result.Succeeded
                        ? SetupErrorCodes.Ok
                        : resultCode)}",
            $"OperationDurationMs={
                DurationToken(context.OperationDuration.TotalMilliseconds)}",
            $"PackageValidation={PackageValidation(context.Result, stages)}",
            $"RecoveryJournal={RecoveryJournal(context.Recovery)}",
            $"Service={ServiceStatus(context.Result, context.Recovery, stages)}",
            $"FirewallDecisionCodes={
                (firewallDecisionCodes.Count == 0
                    ? "NONE"
                    : string.Join(",", firewallDecisionCodes))}",
            $"LocalTcp18443={
                LocalTcpStatus(context.Result, context.Recovery, stages)}",
            $"Readiness={ReadinessStatus(context.Result, stages)}",
            $"AgentHealthCode={AgentHealthCode(context.Result, context.Recovery)}",
            $"AgentRestartObserved={
                (context.Result.AgentRestartObserved ||
                 context.Recovery.AgentRestartObserved
                    ? "TRUE"
                    : "FALSE")}",
            $"ServiceRunningObserved={
                BooleanToken(
                    context.Result.AgentServiceRunningObserved ||
                    context.Recovery.AgentServiceRunningObserved)}",
            $"ListenerOwnedObserved={
                BooleanToken(
                    context.Result.AgentListenerOwnedObserved ||
                    context.Recovery.AgentListenerOwnedObserved)}",
            $"HttpAttemptCount={
                AttemptCountToken(
                    Math.Max(
                        context.Result.AgentHttpAttemptCount,
                        context.Recovery.AgentHttpAttemptCount))}",
            $"LastTransportPhase={
                TransportPhaseToken(
                    LastTransportPhase(
                        context.Result,
                        context.Recovery))}",
            $"StageCount={stages.Count.ToString(CultureInfo.InvariantCulture)}"
        };

        for (var index = 0; index < stages.Count; index++)
        {
            var stage = stages[index];
            var prefix = $"Stage.{index + 1:D2}";
            lines.Add($"{prefix}.Code={stage.Code}");
            lines.Add($"{prefix}.Status={StageStateToken(stage.State)}");
            lines.Add($"{prefix}.DurationMs={MillisecondsToken(stage.DurationMilliseconds)}");
            lines.Add($"{prefix}.ElapsedMs={MillisecondsToken(stage.ElapsedMilliseconds)}");
        }

        return string.Join("\r\n", lines);
    }

    public static string CreateSupportCode(SetupFieldDiagnosticContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Result);
        ArgumentNullException.ThrowIfNull(context.Recovery);
        if (context.Result.Succeeded)
        {
            throw new ArgumentException(
                "SWD1 support codes are generated only for failed operations.",
                nameof(context));
        }

        var resultCode = AllowedCode(
            context.Result.Code,
            AllowedErrorCodes);
        var primaryCode = AllowedCode(
            context.Result.PrimaryFailureCode,
            AllowedErrorCodes,
            resultCode);
        var stages = BuildStages(context.Result);
        var rollbackCodes = context.Result.RollbackFailureCodes
            .Concat(context.Recovery.RollbackFailureCodes)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var operation = string.Equals(
            context.Operation,
            "recovery-inspection",
            StringComparison.Ordinal)
            ? "recovery"
            : context.Operation;
        var payload = Swd1AgentPayloadBuilder.Build(
            context.ProductVersion,
            operation,
            resultCode,
            primaryCode,
            rollbackCodes,
            RecoveryJournal(context.Recovery),
            ServiceStatus(context.Result, context.Recovery, stages),
            LocalTcpStatus(context.Result, context.Recovery, stages),
            ReadinessStatus(context.Result, stages),
            PackageValidation(context.Result, stages),
            BuildFirewallDecisionCodes(context.Result, stages),
            reserved: AgentHealthSwd1Code(context.Result, context.Recovery));
        return Swd1SupportCode.Encode(payload);
    }

    private static string AgentHealthCode(
        SetupOperationResult result,
        PendingRecoveryInspection recovery)
    {
        var value = result.AgentHealthCode ?? recovery.AgentHealthCode;
        return Enum.TryParse<AgentHealthProbeCode>(
            value,
            ignoreCase: false,
            out var code)
            ? code switch
            {
                AgentHealthProbeCode.HttpsTlsFailed => "HTTPS_TLS_FAILED",
                AgentHealthProbeCode.HttpsRequestTimeout =>
                    "HTTPS_REQUEST_TIMEOUT",
                AgentHealthProbeCode.HttpsConnectionReset =>
                    "HTTPS_CONNECTION_RESET",
                AgentHealthProbeCode.HttpsEof => "HTTPS_EOF",
                AgentHealthProbeCode.HttpsConnectFailed =>
                    "HTTPS_CONNECT_FAILED",
                _ => code.ToString().ToUpperInvariant()
            }
            : NotRun;
    }

    private static byte AgentHealthSwd1Code(
        SetupOperationResult result,
        PendingRecoveryInspection recovery)
    {
        var value = result.AgentHealthCode ?? recovery.AgentHealthCode;
        if (!Enum.TryParse<AgentHealthProbeCode>(
                value,
                ignoreCase: false,
                out var code) ||
            code == AgentHealthProbeCode.Ready)
        {
            return (byte)Swd1AgentHealthCode.NotRecorded;
        }

        return code is
            AgentHealthProbeCode.HttpsTlsFailed or
            AgentHealthProbeCode.HttpsRequestTimeout or
            AgentHealthProbeCode.HttpsConnectionReset or
            AgentHealthProbeCode.HttpsEof or
            AgentHealthProbeCode.HttpsConnectFailed
            ? (byte)Swd1AgentHealthCode.HttpsRequestFailed
            : (byte)code;
    }

    private static IReadOnlyList<SetupStageDiagnostic> BuildStages(
        SetupOperationResult result)
    {
        if (result.DiagnosticMetadata is { } metadata)
        {
            return metadata.Stages
                .Select(stage => stage with
                {
                    Code = AllowedCode(stage.Code, AllowedStageCodes)
                })
                .ToArray();
        }

        return result.Steps
            .Select(step => new SetupStageDiagnostic(
                AllowedCode(step.Code, AllowedStageCodes),
                step.State,
                -1,
                -1))
            .ToArray();
    }

    private static IReadOnlyList<string> BuildFirewallDecisionCodes(
        SetupOperationResult result,
        IReadOnlyList<SetupStageDiagnostic> stages)
    {
        var codes = stages
            .Select(stage => stage.Code)
            .Concat(
                result.DiagnosticMetadata?.SafeDecisionCodes ??
                Array.Empty<string>())
            .Append(result.Code)
            .Concat(result.RollbackFailureCodes)
            .Where(AllowedFirewallDecisionCodes.Contains)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
        return codes;
    }

    private static string PackageValidation(
        SetupOperationResult result,
        IReadOnlyList<SetupStageDiagnostic> stages)
    {
        if (result.Code is
            SetupErrorCodes.PackageNotFound or
            SetupErrorCodes.ManifestInvalid or
            SetupErrorCodes.PackageHashMismatch)
        {
            return "FAIL";
        }

        return HasStage(stages, "PACKAGE_VALID") ? "PASS" : NotRun;
    }

    private static string RecoveryJournal(PendingRecoveryInspection recovery) =>
        !recovery.Exists
            ? "NONE"
            : recovery.CanRecover
                ? "PENDING_RECOVERABLE"
                : "PENDING_BLOCKED";

    private static string ServiceStatus(
        SetupOperationResult result,
        PendingRecoveryInspection recovery,
        IReadOnlyList<SetupStageDiagnostic> stages)
    {
        if (result.Code is
            SetupErrorCodes.ServiceFailed or
            SetupErrorCodes.RollbackServiceStopFailed or
            SetupErrorCodes.RollbackServiceRestoreFailed)
        {
            return "FAIL";
        }

        if (HasStage(stages, "AGENT_READY"))
        {
            return "RUNNING_READY";
        }

        if (HasStage(stages, "SERVICE_CONFIGURED"))
        {
            return "CONFIGURED";
        }

        if (HasStage(stages, "SERVICE_NOT_INSTALLED"))
        {
            return "NOT_INSTALLED";
        }

        if (HasStage(stages, "SERVICE_FOUND"))
        {
            return "FOUND";
        }

        return recovery.ServiceState switch
        {
            "running" => "RUNNING",
            "stopped" => "STOPPED",
            "missing" => "NOT_INSTALLED",
            _ => "UNKNOWN"
        };
    }

    private static string LocalTcpStatus(
        SetupOperationResult result,
        PendingRecoveryInspection recovery,
        IReadOnlyList<SetupStageDiagnostic> stages)
    {
        if (HasStage(stages, "AGENT_READY"))
        {
            return "PASS";
        }

        if (result.AgentListenerOwnedObserved ||
            recovery.AgentListenerOwnedObserved)
        {
            return "PASS_OBSERVED";
        }

        return result.Code == SetupErrorCodes.HealthFailed ||
               HasStage(stages, "AGENT_NOT_READY")
            ? "NOT_CONFIRMED"
            : NotRun;
    }

    private static string ReadinessStatus(
        SetupOperationResult result,
        IReadOnlyList<SetupStageDiagnostic> stages)
    {
        if (HasStage(stages, "AGENT_READY"))
        {
            return "PASS";
        }

        return result.Code == SetupErrorCodes.HealthFailed ||
               HasStage(stages, "AGENT_NOT_READY")
            ? "FAIL"
            : NotRun;
    }

    private static string RecommendedActionCode(string code) =>
        code switch
        {
            SetupErrorCodes.Ok => "NONE",
            SetupErrorCodes.PackageNotFound or
            SetupErrorCodes.ManifestInvalid or
            SetupErrorCodes.PackageHashMismatch => "REPLACE_RELEASE_PACKAGE",
            SetupErrorCodes.ViewerIpInvalid => "ENTER_VIEWER_FIXED_IPV4",
            SetupErrorCodes.NetworkSelectionInvalid => "SELECT_MANAGEMENT_NETWORK",
            SetupErrorCodes.ExistingNetworksNotLoaded =>
                "REVIEW_EXISTING_NETWORKS",
            SetupErrorCodes.AdministratorRequired => "RUN_AS_ADMINISTRATOR",
            SetupErrorCodes.PathInvalid or
            SetupErrorCodes.PathUntrusted or
            SetupErrorCodes.PathNotWritable => "CHECK_INSTALL_PERMISSIONS",
            SetupErrorCodes.ConfigurationInvalid => "REVIEW_CONFIGURATION",
            SetupErrorCodes.ServiceFailed => "CHECK_WINDOWS_SERVICE",
            SetupErrorCodes.FirewallFailed => "CHECK_FIREWALL_POLICY",
            SetupErrorCodes.HealthFailed => "CHECK_AGENT_READINESS",
            SetupErrorCodes.RollbackFailed or
            SetupErrorCodes.RecoveryRequired or
            SetupErrorCodes.RollbackStateMismatch or
            SetupErrorCodes.RollbackServiceStopFailed or
            SetupErrorCodes.RollbackFileRestoreFailed or
            SetupErrorCodes.RollbackDataCleanupFailed or
            SetupErrorCodes.RollbackServiceRestoreFailed or
            SetupErrorCodes.RollbackHttpsFirewallRestoreFailed or
             SetupErrorCodes.RollbackLegacyFirewallRestoreFailed or
             SetupErrorCodes.RollbackJournalWriteFailed or
             SetupErrorCodes.RollbackEvidenceCleanupFailed or
             SetupErrorCodes.RollbackStagingCleanupFailed or
             SetupErrorCodes.RollbackBackupCleanupFailed or
             SetupErrorCodes.RollbackFailedDirectoryCleanupFailed or
             SetupErrorCodes.RollbackJournalCleanupFailed =>
                 "RUN_OR_REVIEW_RECOVERY",
            SetupErrorCodes.AlreadyRunning => "WAIT_AND_RETRY",
            SetupErrorCodes.Cancelled => "RETRY_WHEN_READY",
            SetupErrorCodes.DiagnosticWriteFailed =>
                "CHOOSE_WRITABLE_LOCATION",
            _ => "COLLECT_DIAGNOSTIC"
        };

    private static bool HasStage(
        IReadOnlyList<SetupStageDiagnostic> stages,
        string code) =>
        stages.Any(stage =>
            string.Equals(stage.Code, code, StringComparison.Ordinal));

    private static string StageStateToken(SetupStepState state) =>
        state switch
        {
            SetupStepState.Pending => "PENDING",
            SetupStepState.Running => "RUNNING",
            SetupStepState.Succeeded => "SUCCESS",
            SetupStepState.Failed => "FAILURE",
            SetupStepState.Warning => "WARNING",
            SetupStepState.Information => "INFORMATION",
            _ => Unavailable
        };

    private static string ArchitectureToken(string? value) =>
        value?.Trim().ToUpperInvariant() switch
        {
            "X64" => "X64",
            "X86" => "X86",
            "ARM64" => "ARM64",
            "ARM" => "ARM",
            _ => Unavailable
        };

    private static string WindowsBuildToken(string? value)
    {
        if (!Version.TryParse(value, out var version) ||
            version.Major < 0 ||
            version.Minor < 0)
        {
            return Unavailable;
        }

        return string.Join(
            "_",
            new[]
            {
                "WIN",
                version.Major.ToString(CultureInfo.InvariantCulture),
                version.Minor.ToString(CultureInfo.InvariantCulture),
                Math.Max(0, version.Build).ToString(CultureInfo.InvariantCulture),
                Math.Max(0, version.Revision).ToString(CultureInfo.InvariantCulture)
            });
    }

    private static string AllowedCode(
        string? value,
        HashSet<string> allowlist,
        string fallback = Unavailable) =>
        value is not null && allowlist.Contains(value)
            ? value
            : fallback;

    private static string SafeToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Unavailable;
        }

        var builder = new StringBuilder(capacity: Math.Min(value.Length, 64));
        foreach (var character in value.Trim())
        {
            if (builder.Length == 64)
            {
                break;
            }

            if (char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-')
            {
                builder.Append(character);
            }
            else
            {
                return Unavailable;
            }
        }

        return builder.Length == 0 ? Unavailable : builder.ToString();
    }

    private static string DurationToken(double milliseconds) =>
        double.IsFinite(milliseconds) && milliseconds >= 0
            ? Math.Min(
                    86_400_000,
                    (long)Math.Round(
                        milliseconds,
                        MidpointRounding.AwayFromZero))
                .ToString(CultureInfo.InvariantCulture)
            : "unknown";

    private static string MillisecondsToken(long milliseconds) =>
        milliseconds >= 0
            ? Math.Min(86_400_000, milliseconds)
                .ToString(CultureInfo.InvariantCulture)
            : "unknown";

    private static string BooleanToken(bool value) =>
        value ? "TRUE" : "FALSE";

    private static string AttemptCountToken(int count) =>
        Math.Clamp(count, 0, 10_000).ToString(CultureInfo.InvariantCulture);

    private static string TransportPhaseToken(
        AgentHealthTransportPhase phase) =>
        phase switch
        {
            AgentHealthTransportPhase.NotStarted => "NOT_STARTED",
            AgentHealthTransportPhase.ListenerOwned => "LISTENER_OWNED",
            AgentHealthTransportPhase.RequestStarted => "REQUEST_STARTED",
            AgentHealthTransportPhase.ResponseHeaders => "RESPONSE_HEADERS",
            AgentHealthTransportPhase.ResponseBody => "RESPONSE_BODY",
            AgentHealthTransportPhase.ReadinessValidated =>
                "READINESS_VALIDATED",
            _ => "NOT_STARTED"
        };

    private static AgentHealthTransportPhase LastTransportPhase(
        SetupOperationResult result,
        PendingRecoveryInspection recovery) =>
        result.AgentLastTransportPhase != AgentHealthTransportPhase.NotStarted
            ? result.AgentLastTransportPhase
            : recovery.AgentLastTransportPhase;
}

internal static class SetupFieldDiagnosticWriter
{
    private static readonly UTF8Encoding Utf8WithBom =
        new(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);

    public static void Write(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new DirectoryNotFoundException();
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       8 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, Utf8WithBom))
            {
                writer.Write(contents);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Best-effort cleanup must not hide the original write result.
            }
        }
    }
}

internal enum SetupFieldDiagnosticSaveState
{
    Cancelled,
    Succeeded,
    Failed
}

internal readonly record struct SetupFieldDiagnosticSaveResult(
    SetupFieldDiagnosticSaveState State,
    string ErrorCode);

internal static class SetupFieldDiagnosticSaveCoordinator
{
    public static SetupFieldDiagnosticSaveResult Save(
        Func<string?> selectPath,
        Func<string> createContents,
        Action<string, string> write)
    {
        ArgumentNullException.ThrowIfNull(selectPath);
        ArgumentNullException.ThrowIfNull(createContents);
        ArgumentNullException.ThrowIfNull(write);

        try
        {
            var path = selectPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                return new SetupFieldDiagnosticSaveResult(
                    SetupFieldDiagnosticSaveState.Cancelled,
                    SetupErrorCodes.Ok);
            }

            write(path, createContents());
            return new SetupFieldDiagnosticSaveResult(
                SetupFieldDiagnosticSaveState.Succeeded,
                SetupErrorCodes.Ok);
        }
        catch
        {
            return new SetupFieldDiagnosticSaveResult(
                SetupFieldDiagnosticSaveState.Failed,
                SetupErrorCodes.DiagnosticWriteFailed);
        }
    }
}
