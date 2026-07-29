using System.Globalization;
using System.Text;
using SamsungSwitchWatch.Agent.Setup.Deployment;

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
            RecoverVisible: recovery.Exists,
            RecoverEnabled:
                !busy &&
                !diagnosticsOnly &&
                recovery.Exists &&
                recovery.CanRecover);
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
            RollbackFailureCodes = inspection.FailureCodes
        };
    }

    public static IReadOnlyList<SetupStepResult> BuildSteps(
        SetupOperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var primaryCode = NormalizeCode(result.PrimaryFailureCode);
        var rollbackCodes = result.RollbackFailureCodes
            .Select(NormalizeCode)
            .Where(code => code is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var rollbackCodeSet = rollbackCodes.ToHashSet(StringComparer.Ordinal);

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
                "이전 상태 복구 실패",
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
            _ => "이전 상태 복구의 일부 단계를 완료하지 못했습니다."
        };
}

internal sealed record SetupFailureDiagnosticContext(
    string ProductVersion,
    DateTimeOffset UtcTimestamp,
    string Operation,
    SetupOperationResult Result,
    PendingRecoveryInspection Recovery);

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

    private static string BooleanToken(bool value) =>
        value ? "true" : "false";

    private static string EvidenceToken(
        PendingRecoveryInspection recovery,
        bool value) =>
        recovery.EvidenceStateKnown
            ? BooleanToken(value)
            : "unknown";
}
