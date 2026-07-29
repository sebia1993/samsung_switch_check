using SamsungSwitchWatch.Agent.Setup.Deployment;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class SetupUiPresentationTests
{
    [Fact]
    public void PendingRecoveryResult_PreservesOriginalFailureCodeAndMessage()
    {
        var inspection = new PendingRecoveryInspection(
            true,
            true,
            SetupErrorCodes.RecoveryRequired,
            "중단된 이전 작업이 있습니다.")
        {
            PrimaryFailureCode = SetupErrorCodes.ServiceFailed,
            PrimaryFailureMessage = "서비스 등록 단계에서 실패했습니다.",
            RollbackFailureCodes =
            [
                SetupErrorCodes.RollbackServiceRestoreFailed
            ]
        };

        var result =
            SetupResultPresentation.BuildPendingRecoveryResult(inspection);
        var steps = SetupResultPresentation.BuildSteps(result);

        Assert.Equal(
            SetupErrorCodes.ServiceFailed,
            result.PrimaryFailureCode);
        Assert.Equal(
            "서비스 등록 단계에서 실패했습니다.",
            result.PrimaryFailureMessage);
        Assert.Contains(
            steps,
            step =>
                step.Code == SetupErrorCodes.ServiceFailed &&
                step.Message == "서비스 등록 단계에서 실패했습니다.");
        Assert.Contains(
            steps,
            step =>
                step.Code ==
                SetupErrorCodes.RollbackServiceRestoreFailed);
    }

    [Fact]
    public void RecoveryPolicy_SafePendingRequiresSeparateRecoveryBeforeInstall()
    {
        var inspection = new PendingRecoveryInspection(
            Exists: true,
            CanRecover: true,
            SetupErrorCodes.RecoveryRequired,
            "recovery required");

        var state = SetupRecoveryActionPolicy.Evaluate(
            diagnosticsOnly: false,
            busy: false,
            inspection);

        Assert.False(state.InstallEnabled);
        Assert.True(state.RecoverVisible);
        Assert.True(state.RecoverEnabled);
    }

    [Fact]
    public void RecoveryPolicy_UnsafePendingDisablesRecoveryAndInstall()
    {
        var inspection = new PendingRecoveryInspection(
            Exists: true,
            CanRecover: false,
            SetupErrorCodes.RecoveryRequired,
            "administrator review required");

        var state = SetupRecoveryActionPolicy.Evaluate(
            diagnosticsOnly: false,
            busy: false,
            inspection);

        Assert.False(state.InstallEnabled);
        Assert.True(state.RecoverVisible);
        Assert.False(state.RecoverEnabled);
    }

    [Fact]
    public void RecoveryPolicy_CompletedRecoveryEnablesInstallWithoutStartingIt()
    {
        var state = SetupRecoveryActionPolicy.Evaluate(
            diagnosticsOnly: false,
            busy: false,
            PendingRecoveryInspection.None);

        Assert.True(state.InstallEnabled);
        Assert.False(state.RecoverVisible);
        Assert.False(state.RecoverEnabled);
    }

    [Fact]
    public void ResultPresentation_SeparatesPrimaryFailureAndRollbackSubcodes()
    {
        var result = SetupOperationResult.Failure(
            SetupErrorCodes.RollbackFailed,
            "rollback failed",
            [
                new SetupStepResult(
                    SetupErrorCodes.ServiceFailed,
                    "설치 실패",
                    SetupStepState.Failed,
                    "service failed"),
                new SetupStepResult(
                    SetupErrorCodes.RollbackFailed,
                    "이전 상태 복구",
                    SetupStepState.Failed,
                    "rollback failed"),
                new SetupStepResult(
                    SetupErrorCodes.RollbackFileRestoreFailed,
                    "이전 상태 복구",
                    SetupStepState.Failed,
                    "file restore failed")
            ]) with
        {
            PrimaryFailureCode = SetupErrorCodes.ServiceFailed,
            PrimaryFailureMessage = "서비스 설치 단계에서 실패했습니다.",
            RollbackFailureCodes =
            [
                SetupErrorCodes.RollbackFileRestoreFailed,
                SetupErrorCodes.RollbackFileRestoreFailed,
                SetupErrorCodes.RollbackServiceRestoreFailed
            ]
        };

        var steps = SetupResultPresentation.BuildSteps(result);

        Assert.DoesNotContain(
            steps,
            step => step.Code == SetupErrorCodes.RollbackFailed);
        var primary = Assert.Single(
            steps,
            step => step.Label == "원래 설치 실패");
        Assert.Equal(SetupErrorCodes.ServiceFailed, primary.Code);
        Assert.Equal(
            "서비스 설치 단계에서 실패했습니다.",
            primary.Message);
        Assert.Single(
            steps,
            step => step.Code == SetupErrorCodes.RollbackFileRestoreFailed);
        Assert.Single(
            steps,
            step => step.Code == SetupErrorCodes.RollbackServiceRestoreFailed);
    }

    [Fact]
    public void ResultPresentation_KeepsGenericRollbackFailureWithoutSubcodes()
    {
        var result = SetupOperationResult.Failure(
            SetupErrorCodes.RollbackFailed,
            "rollback failed",
            [
                new SetupStepResult(
                    SetupErrorCodes.RollbackFailed,
                    "이전 상태 복구",
                    SetupStepState.Failed,
                    "rollback failed")
            ]);

        var step = Assert.Single(SetupResultPresentation.BuildSteps(result));

        Assert.Equal(SetupErrorCodes.RollbackFailed, step.Code);
    }

    [Fact]
    public void FailureDiagnostic_ContainsOnlySanitizedOperationalMetadata()
    {
        var result = SetupOperationResult.Failure(
            SetupErrorCodes.RollbackFailed,
            @"C:\Program Files\secret at 10.20.30.40",
            []) with
        {
            PrimaryFailureCode = SetupErrorCodes.ServiceFailed,
            PrimaryFailureMessage =
                @"DOMAIN\operator C:\ProgramData transaction-123",
            RollbackFailureCodes =
            [
                SetupErrorCodes.RollbackFileRestoreFailed
            ]
        };
        var recovery = new PendingRecoveryInspection(
            Exists: true,
            CanRecover: true,
            SetupErrorCodes.RecoveryRequired,
            "safe")
        {
            JournalFormatVersion = 2,
            JournalStage = @"C:\ProgramData\secret",
            PrimaryFailureCode = SetupErrorCodes.ServiceFailed,
            RollbackFailureCodes =
            [
                SetupErrorCodes.RollbackServiceRestoreFailed
            ],
            ServiceState = "stopped",
            InstallDirectoryExists = true,
            StagingDirectoryExists = true,
            BackupDirectoryExists = true,
            FailedDirectoryExists = false,
            DataDirectoryExists = true
        };

        var text = SetupFailureDiagnosticFormatter.Format(
            new SetupFailureDiagnosticContext(
                "0.10.7-poc",
                new DateTimeOffset(
                    2026,
                    7,
                    29,
                    1,
                    2,
                    3,
                    TimeSpan.Zero),
                "recovery",
                result,
                recovery));

        Assert.Contains("ProductVersion=0.10.7-poc", text);
        Assert.Contains("UtcTimestamp=2026-07-29T01:02:03.0000000Z", text);
        Assert.Contains("Operation=recovery", text);
        Assert.Contains(
            $"PrimaryFailureCode={SetupErrorCodes.ServiceFailed}",
            text);
        Assert.Contains(
            SetupErrorCodes.RollbackFileRestoreFailed,
            text);
        Assert.Contains(
            SetupErrorCodes.RollbackServiceRestoreFailed,
            text);
        Assert.Contains("RecoveryJournalExists=true", text);
        Assert.Contains("RecoveryCanRun=true", text);
        Assert.Contains("JournalFormatVersion=2", text);
        Assert.Contains("JournalStage=unavailable", text);
        Assert.Contains("ServiceState=stopped", text);
        Assert.Contains("InstallDirectoryExists=true", text);
        Assert.Contains("StagingDirectoryExists=true", text);
        Assert.Contains("BackupDirectoryExists=true", text);
        Assert.Contains("FailedDirectoryExists=false", text);
        Assert.Contains("DataDirectoryExists=true", text);
        Assert.DoesNotContain("10.20.30.40", text);
        Assert.DoesNotContain(@"C:\", text);
        Assert.DoesNotContain("DOMAIN", text);
        Assert.DoesNotContain("operator", text);
        Assert.DoesNotContain("transaction-123", text);
    }

    [Fact]
    public void FailureDiagnostic_RejectsSuccessfulResult()
    {
        var context = new SetupFailureDiagnosticContext(
            "0.10.7-poc",
            DateTimeOffset.UtcNow,
            "install",
            SetupOperationResult.Success("done", []),
            PendingRecoveryInspection.None);

        Assert.Throws<ArgumentException>(
            () => SetupFailureDiagnosticFormatter.Format(context));
    }

    [Fact]
    public void FailureDiagnostic_UsesUnknownForUnsafeEvidence()
    {
        var result = SetupOperationResult.Failure(
            SetupErrorCodes.Unexpected,
            "failed",
            []);
        var recovery = new PendingRecoveryInspection(
            true,
            false,
            SetupErrorCodes.RecoveryRequired,
            "unsafe")
        {
            EvidenceStateKnown = false
        };

        var text = SetupFailureDiagnosticFormatter.Format(
            new SetupFailureDiagnosticContext(
                "0.10.7-poc",
                DateTimeOffset.UnixEpoch,
                "recovery",
                result,
                recovery));

        Assert.Contains("InstallDirectoryExists=unknown", text);
        Assert.Contains("StagingDirectoryExists=unknown", text);
        Assert.Contains("BackupDirectoryExists=unknown", text);
        Assert.Contains("FailedDirectoryExists=unknown", text);
        Assert.Contains("DataDirectoryExists=unknown", text);
    }
}
