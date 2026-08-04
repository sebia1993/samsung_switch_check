using SamsungSwitchWatch.Agent.Setup.Deployment;
using SamsungSwitchWatch.Support;

namespace SamsungSwitchWatch.Agent.Setup.Tests;

public sealed class SetupUiPresentationTests
{
    [Fact]
    public void InstallCompletion_SuccessWithoutWarningIsReadyForRemoteConnection()
    {
        var result = SetupOperationResult.Success(
            "installed",
            [
                new SetupStepResult(
                    "SETUP_COMPLETED",
                    "설치 완료",
                    SetupStepState.Succeeded,
                    "completed")
            ]);

        var completion = SetupInstallCompletionPolicy.Evaluate(result);

        Assert.Equal(
            SetupInstallCompletionSeverity.Success,
            completion.Severity);
        Assert.Equal(
            "설치 완료 · 원격 연결 준비됨",
            completion.StatusText);
        Assert.Contains(
            "Agent 연결 테스트",
            completion.GuidanceText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InstallCompletion_RemoteAccessWarningRequiresViewerConnectionTest()
    {
        var result = SetupOperationResult.Success(
            "installed with warning",
            [
                new SetupStepResult(
                    SetupErrorCodes.FirewallRemoteAccessUnconfirmed,
                    "원격 연결 확인 필요",
                    SetupStepState.Warning,
                    "remote access is not confirmed")
            ]);

        var completion = SetupInstallCompletionPolicy.Evaluate(result);

        Assert.Equal(
            SetupInstallCompletionSeverity.Warning,
            completion.Severity);
        Assert.Equal(
            "설치 완료 · 원격 Viewer 연결 확인 필요",
            completion.StatusText);
        Assert.Equal(
            "Viewer에서 Agent 연결 테스트를 실행해 원격 연결을 확인하세요.",
            completion.GuidanceText);
    }

    [Fact]
    public void InstallCompletion_LocalConnectionWarningKeepsInstallCompleted()
    {
        var result = SetupOperationResult.Success(
            "installed with warning",
            [
                new SetupStepResult(
                    "PACKAGE_VALID",
                    "package",
                    SetupStepState.Succeeded,
                    "valid"),
                new SetupStepResult(
                    "SERVICE_CONFIGURED",
                    "service",
                    SetupStepState.Succeeded,
                    "configured"),
                new SetupStepResult(
                    SetupErrorCodes.AgentLocalConnectionUnconfirmed,
                    "Agent 연결 확인",
                    SetupStepState.Warning,
                    "not confirmed")
            ]);

        var completion = SetupInstallCompletionPolicy.Evaluate(result);

        Assert.Equal(
            SetupInstallCompletionSeverity.Warning,
            completion.Severity);
        Assert.Equal("설치 완료 · 연결 확인 필요", completion.StatusText);
        Assert.Contains(
            "Agent는 설치되어 실행 중",
            completion.GuidanceText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InstallCompletion_LocalHttpsFailureRemainsInstallationFailure()
    {
        var result = SetupOperationResult.Failure(
            SetupErrorCodes.HealthFailed,
            "local HTTPS readiness failed",
            [
                new SetupStepResult(
                    SetupErrorCodes.HealthFailed,
                    "로컬 HTTPS 응답",
                    SetupStepState.Failed,
                    "failed")
            ]);

        var completion = SetupInstallCompletionPolicy.Evaluate(result);

        Assert.Equal(
            SetupInstallCompletionSeverity.Error,
            completion.Severity);
        Assert.Equal(
            $"설치 실패 · {SetupErrorCodes.HealthFailed}",
            completion.StatusText);
        Assert.DoesNotContain(
            "설치 완료",
            completion.StatusText,
            StringComparison.Ordinal);
    }

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
            ],
            AgentHealthCode =
                AgentHealthProbeCode.HttpsConnectionReset.ToString(),
            AgentRestartObserved = true,
            AgentServiceRunningObserved = true,
            AgentListenerOwnedObserved = true,
            AgentHttpAttemptCount = 2,
            AgentLastTransportPhase =
                AgentHealthTransportPhase.RequestStarted
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
        Assert.Equal(
            AgentHealthProbeCode.HttpsConnectionReset.ToString(),
            result.AgentHealthCode);
        Assert.True(result.AgentRestartObserved);
        Assert.True(result.AgentServiceRunningObserved);
        Assert.True(result.AgentListenerOwnedObserved);
        Assert.Equal(2, result.AgentHttpAttemptCount);
        Assert.Equal(
            AgentHealthTransportPhase.RequestStarted,
            result.AgentLastTransportPhase);
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
        Assert.False(state.RecoverVisible);
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
    public void RecoveryCompletion_SuccessWithoutJournalIsReadyForManualInstall()
    {
        var result = SetupOperationResult.Success(
            "recovery completed",
            [
                new SetupStepResult(
                    "ROLLBACK_COMPLETED",
                    "이전 상태 복구",
                    SetupStepState.Succeeded,
                    "completed")
            ]);

        var completion = SetupRecoveryCompletionPolicy.Evaluate(
            result,
            PendingRecoveryInspection.None);
        var actions = SetupRecoveryActionPolicy.Evaluate(
            diagnosticsOnly: false,
            busy: false,
            PendingRecoveryInspection.None);

        Assert.True(completion.ReadyForInstall);
        Assert.False(completion.UseInspectionResult);
        Assert.Equal(
            SetupRecoveryCompletionSeverity.Success,
            completion.Severity);
        Assert.Equal("복구 완료 · 설치 준비됨", completion.StatusText);
        Assert.True(actions.InstallEnabled);
        Assert.False(actions.RecoverVisible);
    }

    [Fact]
    public void RecoveryCompletion_SuccessWithPendingJournalRequiresRecheck()
    {
        var result = SetupOperationResult.Success(
            "recovery API completed",
            [
                new SetupStepResult(
                    "ROLLBACK_COMPLETED",
                    "이전 상태 복구",
                    SetupStepState.Succeeded,
                    "completed")
            ]);
        var inspection = new PendingRecoveryInspection(
            Exists: true,
            CanRecover: true,
            SetupErrorCodes.RecoveryRequired,
            "journal remains");

        var completion = SetupRecoveryCompletionPolicy.Evaluate(
            result,
            inspection);
        var actions = SetupRecoveryActionPolicy.Evaluate(
            diagnosticsOnly: false,
            busy: false,
            inspection);

        Assert.False(completion.ReadyForInstall);
        Assert.True(completion.UseInspectionResult);
        Assert.Equal(
            SetupRecoveryCompletionSeverity.Warning,
            completion.Severity);
        Assert.Contains(
            SetupErrorCodes.RecoveryRequired,
            completion.StatusText,
            StringComparison.Ordinal);
        Assert.False(actions.InstallEnabled);
        Assert.True(actions.RecoverVisible);
        Assert.True(actions.RecoverEnabled);
    }

    [Fact]
    public void RecoveryCompletion_FailureWithPendingJournalPreservesOriginalFailureRows()
    {
        var result = SetupOperationResult.Failure(
            SetupErrorCodes.RollbackFailed,
            "rollback failed",
            [
                new SetupStepResult(
                    SetupErrorCodes.ServiceFailed,
                    "설치 실패",
                    SetupStepState.Failed,
                    "service failed")
            ]) with
        {
            PrimaryFailureCode = SetupErrorCodes.ServiceFailed,
            PrimaryFailureMessage = "original install failure",
            RollbackFailureCodes =
            [
                SetupErrorCodes.RollbackFileRestoreFailed
            ]
        };
        var inspection = new PendingRecoveryInspection(
            Exists: true,
            CanRecover: true,
            SetupErrorCodes.RecoveryRequired,
            "retry is safe");

        var completion = SetupRecoveryCompletionPolicy.Evaluate(
            result,
            inspection);
        var rows = SetupResultPresentation.BuildSteps(result);
        var actions = SetupRecoveryActionPolicy.Evaluate(
            diagnosticsOnly: false,
            busy: false,
            inspection);

        Assert.False(completion.ReadyForInstall);
        Assert.False(completion.UseInspectionResult);
        Assert.Equal(
            SetupRecoveryCompletionSeverity.Error,
            completion.Severity);
        Assert.Equal(
            $"복구 실패 · {SetupErrorCodes.RollbackFailed}",
            completion.StatusText);
        Assert.Equal(
            "설치는 계속 잠겨 있습니다. 아래 원래 실패 원인과 복구 실패 단계를 확인하세요.",
            completion.GuidanceText);
        Assert.False(actions.InstallEnabled);
        Assert.True(actions.RecoverVisible);
        Assert.True(actions.RecoverEnabled);
        Assert.Contains(
            rows,
            row => row.Code == SetupErrorCodes.ServiceFailed);
        Assert.Contains(
            rows,
            row =>
                row.Code ==
                SetupErrorCodes.RollbackFileRestoreFailed);
    }

    [Fact]
    public void RecoveryCompletion_UnsafePendingJournalDisablesRecoveryAndInstall()
    {
        var result = SetupOperationResult.Success(
            "recovery API completed",
            [
                new SetupStepResult(
                    "ROLLBACK_COMPLETED",
                    "이전 상태 복구",
                    SetupStepState.Succeeded,
                    "completed")
            ]);
        var inspection = new PendingRecoveryInspection(
            Exists: true,
            CanRecover: false,
            SetupErrorCodes.RecoveryRequired,
            "unsafe journal");

        var completion = SetupRecoveryCompletionPolicy.Evaluate(
            result,
            inspection);
        var actions = SetupRecoveryActionPolicy.Evaluate(
            diagnosticsOnly: false,
            busy: false,
            inspection);

        Assert.False(completion.ReadyForInstall);
        Assert.True(completion.UseInspectionResult);
        Assert.Equal(
            SetupRecoveryCompletionSeverity.Error,
            completion.Severity);
        Assert.Contains("상태 확인 실패", completion.StatusText);
        Assert.False(actions.InstallEnabled);
        Assert.False(actions.RecoverVisible);
        Assert.False(actions.RecoverEnabled);
    }

    [Fact]
    public void BuildSteps_TargetSpecificCleanupFailureReplacesGenericCleanupRow()
    {
        var result = SetupOperationResult.Failure(
            SetupErrorCodes.RollbackFailed,
            "이전 설치 상태를 완전히 복구하지 못했습니다.",
            [
                new SetupStepResult(
                    SetupErrorCodes.RollbackEvidenceCleanupFailed,
                    "복구 자료 정리",
                    SetupStepState.Failed,
                    "복구 자료를 정리하지 못했습니다."),
                new SetupStepResult(
                    SetupErrorCodes.RollbackJournalCleanupFailed,
                    "복구 기록 정리",
                    SetupStepState.Failed,
                    "복구 작업 기록을 정리하지 못했습니다.")
            ]) with
        {
            RollbackFailureCodes =
            [
                SetupErrorCodes.RollbackEvidenceCleanupFailed,
                SetupErrorCodes.RollbackJournalCleanupFailed
            ]
        };

        var rows = SetupResultPresentation.BuildSteps(result);

        var row = Assert.Single(rows);
        Assert.Equal(SetupErrorCodes.RollbackJournalCleanupFailed, row.Code);
        Assert.Equal("복구 기록 정리", row.Label);
        Assert.Contains("작업 기록", row.Message);
        Assert.DoesNotContain(
            rows,
            item => item.Code == SetupErrorCodes.RollbackEvidenceCleanupFailed);
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
        Assert.Contains("FailedStage=RECOVERY", text);
        Assert.Contains("FailureCategory=CLASSIFIED", text);
        Assert.Contains("FailureStageDurationMs=unknown", text);
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

    [Fact]
    public void FieldDiagnostic_FormatsAllowlistedSetupStateAndStageTimings()
    {
        var steps = new SetupStepRecorder();
        steps.Add(new SetupStepResult(
            "PACKAGE_VALID",
            "package",
            SetupStepState.Succeeded,
            @"10.20.30.40 C:\ProgramData DOMAIN\operator"));
        steps.Add(new SetupStepResult(
            "SERVICE_CONFIGURED",
            "service",
            SetupStepState.Succeeded,
            "secret-password"));
        steps.AddSafeDecisionCode(
            FirewallRuleMismatchCodes.RemoteAddress);
        steps.Add(new SetupStepResult(
            SetupErrorCodes.FirewallFailed,
            "firewall",
            SetupStepState.Failed,
            "192.168.40.20/32"));
        var result = SetupOperationResult.Failure(
            SetupErrorCodes.FirewallFailed,
            "raw exception text",
            steps) with
        {
            PrimaryFailureCode = SetupErrorCodes.FirewallFailed,
            PrimaryFailureMessage = "device output"
        };
        var recovery = new PendingRecoveryInspection(
            true,
            true,
            SetupErrorCodes.RecoveryRequired,
            @"C:\secret")
        {
            ServiceState = "stopped"
        };

        var text = SetupFieldDiagnosticFormatter.Format(
            new SetupFieldDiagnosticContext(
                "0.10.8-poc",
                new DateTimeOffset(
                    2026,
                    7,
                    29,
                    1,
                    2,
                    3,
                    TimeSpan.Zero),
                "10.0.26100.0",
                "X64",
                "install",
                TimeSpan.FromMilliseconds(321),
                result,
                recovery));

        AssertCompactFieldDiagnostic(text);
        Assert.StartsWith("SSW_FIELD_DIAGNOSTIC/2\r\n", text);
        Assert.Contains("Component=AGENT_SETUP", text);
        Assert.Contains("ProductVersion=0.10.8-poc", text);
        Assert.Contains(
            "Environment=20260729T010203000Z|WIN_10_0_26100_0|X64",
            text);
        Assert.Contains("Run=INSTALL|FAILURE|321", text);
        Assert.Contains("FailedStage=FIREWALL", text);
        Assert.Contains(
            $"ErrorCode={SetupErrorCodes.FirewallFailed}",
            text);
        Assert.Contains(
            $"Failure={SetupErrorCodes.FirewallFailed}|CLASSIFIED|unknown",
            text);
        Assert.Contains(
            "Action=CHECK_FIREWALL_POLICY",
            text);
        Assert.Contains(
            "State=PASS|PENDING_RECOVERABLE|CONFIGURED|FAIL|NOT_RUN|NOT_RUN",
            text);
        Assert.Contains("Health=NOT_RUN|FFF|0|NOT_STARTED", text);
        Assert.Contains(
            "Stages=3|PACKAGE_VALID:S>SERVICE_CONFIGURED:S>SETUP_FIREWALL_FAILED:F",
            text);
        Assert.DoesNotContain("10.20.30.40", text);
        Assert.DoesNotContain("192.168.40.20", text);
        Assert.DoesNotContain(@"C:\", text);
        Assert.DoesNotContain("DOMAIN", text);
        Assert.DoesNotContain("operator", text);
        Assert.DoesNotContain("secret-password", text);
        Assert.DoesNotContain("raw exception", text);
        Assert.DoesNotContain("device output", text);
    }

    [Fact]
    public void UnexpectedFailureDiagnosticsPreserveOnlySafeStageCategoryAndDuration()
    {
        var steps = new SetupStepRecorder();
        steps.MarkActiveStage(SetupFailureStage.ServiceStart);
        steps.RecordUnexpectedFailure(
            new UnauthorizedAccessException(
                @"sensitive DOMAIN\operator C:\ProgramData"));
        steps.Add(new SetupStepResult(
            SetupErrorCodes.Unexpected,
            "install",
            SetupStepState.Failed,
            "safe"));
        var result = SetupOperationResult.Failure(
            SetupErrorCodes.Unexpected,
            "safe",
            steps);
        var recovery = PendingRecoveryInspection.None;

        var copied = SetupFailureDiagnosticFormatter.Format(
            new SetupFailureDiagnosticContext(
                "0.10.12-poc",
                DateTimeOffset.UnixEpoch,
                "install",
                result,
                recovery));
        var field = SetupFieldDiagnosticFormatter.Format(
            new SetupFieldDiagnosticContext(
                "0.10.12-poc",
                DateTimeOffset.UnixEpoch,
                "10.0.26100.0",
                "X64",
                "install",
                TimeSpan.Zero,
                result,
                recovery));

        Assert.Contains("FailedStage=SERVICE_START", copied);
        Assert.Contains("FailureCategory=ACCESS_DENIED", copied);
        Assert.Matches(@"FailureStageDurationMs=\d+", copied);
        Assert.Contains("FailedStage=SERVICE_START", field);
        Assert.Matches(
            @"Failure=SETUP_UNEXPECTED\|ACCESS_DENIED\|\d+",
            field);
        AssertCompactFieldDiagnostic(field);

        foreach (var text in new[] { copied, field })
        {
            Assert.DoesNotContain("sensitive", text);
            Assert.DoesNotContain("DOMAIN", text);
            Assert.DoesNotContain(@"C:\", text);
            Assert.DoesNotContain("operator", text);
        }
    }

    [Fact]
    public void FieldDiagnostic_UsesFinalRollbackResultForFailedStage()
    {
        var steps = new SetupStepRecorder();
        steps.MarkActiveStage(SetupFailureStage.ServiceStart);
        steps.RecordUnexpectedFailure(
            new UnauthorizedAccessException("safe"));
        var result = SetupOperationResult.Failure(
            SetupErrorCodes.RollbackFailed,
            "safe",
            steps) with
        {
            PrimaryFailureCode = SetupErrorCodes.HealthFailed,
            RollbackFailureCodes =
                [SetupErrorCodes.RollbackFileRestoreFailed]
        };

        var fieldText = SetupFieldDiagnosticFormatter.Format(
            new SetupFieldDiagnosticContext(
                "0.10.12-poc",
                DateTimeOffset.UnixEpoch,
                "10.0.26100.0",
                "X64",
                "install",
                TimeSpan.Zero,
                result,
                PendingRecoveryInspection.None));
        var failureText = SetupFailureDiagnosticFormatter.Format(
            new SetupFailureDiagnosticContext(
                "0.10.12-poc",
                DateTimeOffset.UnixEpoch,
                "install",
                result,
                PendingRecoveryInspection.None));

        Assert.Contains("FailedStage=RECOVERY", fieldText);
        Assert.Contains(
            $"Failure={SetupErrorCodes.HealthFailed}|CLASSIFIED|unknown",
            fieldText);
        AssertCompactFieldDiagnostic(fieldText);
        Assert.Contains("FailedStage=RECOVERY", failureText);
        Assert.Contains(
            $"PrimaryFailureCode={SetupErrorCodes.HealthFailed}",
            failureText);
        Assert.Contains("FailureCategory=CLASSIFIED", failureText);
        Assert.Contains("FailureStageDurationMs=unknown", failureText);

        Assert.Contains(
            $"ErrorCode={SetupErrorCodes.RollbackFailed}",
            fieldText);
        Assert.Contains(
            $"ResultCode={SetupErrorCodes.RollbackFailed}",
            failureText);
    }

    [Fact]
    public void FieldDiagnostic_SuccessIncludesReadinessWithoutSensitiveValues()
    {
        var steps = new SetupStepRecorder();
        steps.Add(new SetupStepResult(
            "PACKAGE_VALID",
            "package",
            SetupStepState.Succeeded,
            "safe"));
        steps.Add(new SetupStepResult(
            "AGENT_READY",
            "ready",
            SetupStepState.Succeeded,
            "safe"));
        var result = SetupOperationResult.Success("done", steps) with
        {
            AgentServiceRunningObserved = true,
            AgentListenerOwnedObserved = true,
            AgentHttpAttemptCount = 1,
            AgentLastTransportPhase =
                AgentHealthTransportPhase.ReadinessValidated
        };

        var text = SetupFieldDiagnosticFormatter.Format(
            new SetupFieldDiagnosticContext(
                "0.10.8-poc",
                DateTimeOffset.UnixEpoch,
                "10.0.19045.0",
                "x64",
                "preflight",
                TimeSpan.Zero,
                result,
                PendingRecoveryInspection.None));

        AssertCompactFieldDiagnostic(text);
        Assert.Contains("Run=PREFLIGHT|SUCCESS|0", text);
        Assert.Contains("FailedStage=NONE", text);
        Assert.Contains("ErrorCode=OK", text);
        Assert.Contains("Failure=NONE|NOT_RUN|unknown", text);
        Assert.Contains("Action=NONE", text);
        Assert.Contains(
            "State=PASS|NONE|RUNNING_READY|NONE|PASS|PASS",
            text);
        Assert.Contains(
            "Health=NOT_RUN|FTT|1|READINESS_VALIDATED",
            text);
    }

    [Fact]
    public void FieldDiagnostic_SuccessWithLocalConnectionWarningKeepsWarningEvidence()
    {
        var result = SetupOperationResult.Success(
            "installed with warning",
            [
                new SetupStepResult(
                    "PACKAGE_VALID",
                    "package",
                    SetupStepState.Succeeded,
                    "valid"),
                new SetupStepResult(
                    "SERVICE_CONFIGURED",
                    "service",
                    SetupStepState.Succeeded,
                    "configured"),
                new SetupStepResult(
                    SetupErrorCodes.AgentLocalConnectionUnconfirmed,
                    "Agent connection",
                    SetupStepState.Warning,
                    "not confirmed")
            ]) with
        {
            AgentHealthCode = AgentHealthProbeCode.HttpsRequestTimeout.ToString(),
            AgentServiceRunningObserved = true,
            AgentHttpAttemptCount = 2,
            AgentLastTransportPhase = AgentHealthTransportPhase.RequestStarted
        };

        var text = SetupFieldDiagnosticFormatter.Format(
            new SetupFieldDiagnosticContext(
                "0.11.0-poc",
                DateTimeOffset.UnixEpoch,
                "10.0.26100.0",
                "X64",
                "install",
                TimeSpan.FromSeconds(60),
                result,
                PendingRecoveryInspection.None));

        AssertCompactFieldDiagnostic(text);
        Assert.Contains("Run=INSTALL|SUCCESS|60000", text);
        Assert.Contains("FailedStage=NONE", text);
        Assert.Contains(
            $"ErrorCode={SetupErrorCodes.AgentLocalConnectionUnconfirmed}",
            text);
        Assert.Contains("Failure=NONE|NOT_RUN|unknown", text);
        Assert.Contains("Action=CHECK_AGENT_READINESS", text);
        Assert.Contains(
            "State=PASS|NONE|CONFIGURED|NONE|NOT_CONFIRMED|NOT_CONFIRMED",
            text);
    }

    [Fact]
    public void FieldDiagnostic_SuccessWithFirewallWarningKeepsWarningEvidence()
    {
        var result = SetupOperationResult.Success(
            "installed with warning",
            [
                new SetupStepResult(
                    "SERVICE_CONFIGURED",
                    "service",
                    SetupStepState.Succeeded,
                    "configured"),
                new SetupStepResult(
                    SetupErrorCodes.FirewallRemoteAccessUnconfirmed,
                    "firewall",
                    SetupStepState.Warning,
                    "not confirmed")
            ]);

        var text = SetupFieldDiagnosticFormatter.Format(
            new SetupFieldDiagnosticContext(
                "0.11.0-poc",
                DateTimeOffset.UnixEpoch,
                "10.0.26100.0",
                "X64",
                "install",
                TimeSpan.Zero,
                result,
                PendingRecoveryInspection.None));

        AssertCompactFieldDiagnostic(text);
        Assert.Contains(
            $"ErrorCode={SetupErrorCodes.FirewallRemoteAccessUnconfirmed}",
            text);
        Assert.Contains("Action=CHECK_FIREWALL_POLICY", text);
        Assert.Contains(
            "State=NOT_RUN|NONE|CONFIGURED|NOT_CONFIRMED|NOT_RUN|NOT_RUN",
            text);
    }

    [Fact]
    public void HealthFailureDiagnosticsAndSupportCodePreserveSafeCause()
    {
        var result = SetupOperationResult.Failure(
            SetupErrorCodes.HealthFailed,
            "private detail",
            []) with
        {
            PrimaryFailureCode = SetupErrorCodes.HealthFailed,
            AgentHealthCode =
                AgentHealthProbeCode.TcpOwnedByOtherProcess.ToString(),
            AgentRestartObserved = true,
            AgentServiceRunningObserved = true,
            AgentListenerOwnedObserved = true,
            AgentHttpAttemptCount = 3,
            AgentLastTransportPhase = AgentHealthTransportPhase.ResponseBody
        };

        var failureText = SetupFailureDiagnosticFormatter.Format(
            new SetupFailureDiagnosticContext(
                "0.10.11-poc",
                DateTimeOffset.UnixEpoch,
                "install",
                result,
                PendingRecoveryInspection.None));
        var fieldContext = new SetupFieldDiagnosticContext(
            "0.10.11-poc",
            DateTimeOffset.UnixEpoch,
            "10.0.26100.0",
            "X64",
            "install",
            TimeSpan.Zero,
            result,
            PendingRecoveryInspection.None);
        var fieldText = SetupFieldDiagnosticFormatter.Format(fieldContext);
        var code = SetupFieldDiagnosticFormatter.CreateSupportCode(fieldContext);

        Assert.Contains(
            "AgentHealthCode=TcpOwnedByOtherProcess",
            failureText);
        Assert.Contains("AgentRestartObserved=true", failureText);
        Assert.Contains("ServiceRunningObserved=true", failureText);
        Assert.Contains("ListenerOwnedObserved=true", failureText);
        Assert.Contains("HttpAttemptCount=3", failureText);
        Assert.Contains("LastTransportPhase=RESPONSE_BODY", failureText);
        Assert.Contains(
            "Health=TCPOWNEDBYOTHERPROCESS|TTT|3|RESPONSE_BODY",
            fieldText);
        Assert.Contains(
            "|PASS_OBSERVED|FAIL",
            fieldText);
        AssertCompactFieldDiagnostic(fieldText);
        Assert.True(Swd1SupportCode.TryDecode(code, out var decoded));
        Assert.Equal(
            Swd1AgentHealthCode.TcpOwnedByOtherProcess,
            decoded!.Agent!.Value.HealthCode);
        Assert.Equal(
            Swd1CheckState.Passed,
            decoded.Agent.Value.LocalTcp18443);
    }

    [Theory]
    [InlineData(AgentHealthProbeCode.HttpsTlsFailed, "HTTPS_TLS_FAILED")]
    [InlineData(
        AgentHealthProbeCode.HttpsRequestTimeout,
        "HTTPS_REQUEST_TIMEOUT")]
    [InlineData(
        AgentHealthProbeCode.HttpsConnectionReset,
        "HTTPS_CONNECTION_RESET")]
    [InlineData(AgentHealthProbeCode.HttpsEof, "HTTPS_EOF")]
    [InlineData(
        AgentHealthProbeCode.HttpsConnectFailed,
        "HTTPS_CONNECT_FAILED")]
    public void DetailedHttpsFailureCodesKeepSwd1Compatibility(
        AgentHealthProbeCode healthCode,
        string expectedDiagnosticCode)
    {
        var result = SetupOperationResult.Failure(
            SetupErrorCodes.HealthFailed,
            "safe",
            []) with
        {
            PrimaryFailureCode = SetupErrorCodes.HealthFailed,
            AgentHealthCode = healthCode.ToString()
        };
        var context = new SetupFieldDiagnosticContext(
            "0.10.13-poc",
            DateTimeOffset.UnixEpoch,
            "10.0.26100.0",
            "X64",
            "install",
            TimeSpan.Zero,
            result,
            PendingRecoveryInspection.None);

        var text = SetupFieldDiagnosticFormatter.Format(context);
        var failureText = SetupFailureDiagnosticFormatter.Format(
            new SetupFailureDiagnosticContext(
                "0.10.13-poc",
                DateTimeOffset.UnixEpoch,
                "install",
                result,
                PendingRecoveryInspection.None));
        var supportCode = SetupFieldDiagnosticFormatter.CreateSupportCode(context);

        Assert.Contains($"Health={expectedDiagnosticCode}|", text);
        Assert.Contains(
            $"AgentHealthCode={expectedDiagnosticCode}",
            failureText);
        Assert.True(Swd1SupportCode.TryDecode(supportCode, out var decoded));
        Assert.Equal(
            Swd1AgentHealthCode.HttpsRequestFailed,
            decoded!.Agent!.Value.HealthCode);
    }

    [Fact]
    public void SupportCode_UsesFreshRecoveryAndFirewallMismatchState()
    {
        var steps = new SetupStepRecorder();
        steps.AddSafeDecisionCode(FirewallRuleMismatchCodes.RemoteAddress);
        steps.Add(new SetupStepResult(
            SetupErrorCodes.FirewallFailed,
            "firewall",
            SetupStepState.Failed,
            "private detail"));
        var result = SetupOperationResult.Failure(
            SetupErrorCodes.FirewallFailed,
            "private detail",
            steps) with
        {
            PrimaryFailureCode = SetupErrorCodes.FirewallFailed,
            RollbackFailureCodes =
                [SetupErrorCodes.RollbackStagingCleanupFailed]
        };
        var freshRecovery = new PendingRecoveryInspection(
            true,
            false,
            SetupErrorCodes.RollbackFailed,
            "private detail")
        {
            ServiceState = "stopped",
            RollbackFailureCodes =
                [SetupErrorCodes.RollbackJournalCleanupFailed]
        };

        var code = SetupFieldDiagnosticFormatter.CreateSupportCode(
            new SetupFieldDiagnosticContext(
                "0.10.10-poc",
                DateTimeOffset.UnixEpoch,
                "10.0.26100.0",
                "X64",
                "recovery-inspection",
                TimeSpan.Zero,
                result,
                freshRecovery));

        Assert.Equal(24, code.Length);
        Assert.True(Swd1SupportCode.TryDecode(code, out var decoded));
        Assert.Equal("RECOVERY", decoded!.Common.OperationName);
        Assert.Equal(
            Swd1AgentRollbackFlags.StagingCleanup |
            Swd1AgentRollbackFlags.JournalCleanup,
            decoded.Agent!.Value.RollbackFlags);
        Assert.Equal(
            Swd1AgentFirewallFlags.RemoteAddress,
            decoded.Agent.Value.FirewallFlags);
        Assert.Equal(
            Swd1AgentJournalState.PendingBlocked,
            decoded.Agent.Value.JournalState);
        Assert.Equal(
            Swd1AgentServiceState.Stopped,
            decoded.Agent.Value.ServiceState);
    }

    [Fact]
    public void SupportCode_RejectsSuccessfulOperation()
    {
        var result = SetupOperationResult.Success(
            "done",
            [
                new SetupStepResult(
                    SetupErrorCodes.Ok,
                    "done",
                    SetupStepState.Succeeded,
                    "done")
            ]);
        var context = new SetupFieldDiagnosticContext(
            "0.10.10-poc",
            DateTimeOffset.UnixEpoch,
            "10.0.26100.0",
            "X64",
            "preflight",
            TimeSpan.Zero,
            result,
            PendingRecoveryInspection.None);

        Assert.Throws<ArgumentException>(
            () => SetupFieldDiagnosticFormatter.CreateSupportCode(context));
    }

    [Fact]
    public void FieldDiagnostic_ReplacesUntrustedTokensInsteadOfExportingThem()
    {
        const string sensitive = "10.20.30.40";
        var result = new SetupOperationResult(
            false,
            sensitive,
            @"C:\secret",
            [
                new SetupStepResult(
                    sensitive,
                    "label",
                    SetupStepState.Failed,
                    "password")
            ]);

        var text = SetupFieldDiagnosticFormatter.Format(
            new SetupFieldDiagnosticContext(
                @"C:\secret",
                DateTimeOffset.UnixEpoch,
                @"C:\Windows",
                @"DOMAIN\operator",
                sensitive,
                TimeSpan.FromMilliseconds(-1),
                result,
                PendingRecoveryInspection.None));

        Assert.Contains("ProductVersion=UNAVAILABLE", text);
        Assert.Contains(
            "Environment=19700101T000000000Z|UNAVAILABLE|UNAVAILABLE",
            text);
        Assert.Contains("Run=UNAVAILABLE|FAILURE|unknown", text);
        Assert.Contains("FailedStage=UNKNOWN", text);
        Assert.Contains("ErrorCode=UNAVAILABLE", text);
        Assert.Contains("Failure=UNAVAILABLE|CLASSIFIED|unknown", text);
        Assert.Contains("Stages=1|UNAVAILABLE:F", text);
        AssertCompactFieldDiagnostic(text);
        Assert.DoesNotContain(sensitive, text);
        Assert.DoesNotContain(@"C:\", text);
        Assert.DoesNotContain("DOMAIN", text);
        Assert.DoesNotContain("operator", text);
        Assert.DoesNotContain("password", text);
    }

    [Theory]
    [InlineData(SetupErrorCodes.RollbackStagingCleanupFailed)]
    [InlineData(SetupErrorCodes.RollbackBackupCleanupFailed)]
    [InlineData(SetupErrorCodes.RollbackFailedDirectoryCleanupFailed)]
    [InlineData(SetupErrorCodes.RollbackJournalCleanupFailed)]
    public void FieldDiagnostic_PreservesSanitizedCleanupStageCode(string code)
    {
        var steps = new SetupStepRecorder();
        steps.Add(new SetupStepResult(
            code,
            "복구 정리",
            SetupStepState.Failed,
            "안전한 메시지"));
        var result = SetupOperationResult.Failure(
            code,
            "복구 실패",
            steps);

        var text = SetupFieldDiagnosticFormatter.Format(
            new SetupFieldDiagnosticContext(
                "0.10.9-poc",
                DateTimeOffset.UnixEpoch,
                "10.0.26100.0",
                "X64",
                "recovery",
                TimeSpan.Zero,
                result,
                PendingRecoveryInspection.None));

        Assert.Contains($"Stages=1|{code}:F", text);
        Assert.Contains($"ErrorCode={code}", text);
        Assert.Contains("FailedStage=RECOVERY", text);
        Assert.Contains(
            "Action=RUN_OR_REVIEW_RECOVERY",
            text);
        AssertCompactFieldDiagnostic(text);
    }

    [Fact]
    public void FieldDiagnostic_HealthFailureKeepsGenericHttpsObservation()
    {
        var result = SetupOperationResult.Failure(
            SetupErrorCodes.HealthFailed,
            "private detail",
            []) with
        {
            PrimaryFailureCode = SetupErrorCodes.HealthFailed,
            AgentHealthCode =
                AgentHealthProbeCode.HttpsRequestFailed.ToString(),
            AgentServiceRunningObserved = true,
            AgentListenerOwnedObserved = true,
            AgentHttpAttemptCount = 3,
            AgentLastTransportPhase =
                AgentHealthTransportPhase.RequestStarted
        };

        var text = SetupFieldDiagnosticFormatter.Format(
            new SetupFieldDiagnosticContext(
                "0.10.14-poc",
                DateTimeOffset.UnixEpoch,
                "10.0.26100.0",
                "X64",
                "install",
                TimeSpan.FromMilliseconds(64_182),
                result,
                PendingRecoveryInspection.None));

        Assert.Contains("ErrorCode=SETUP_HEALTH_FAILED", text);
        Assert.Contains(
            "Failure=SETUP_HEALTH_FAILED|CLASSIFIED|unknown",
            text);
        Assert.Contains(
            "Health=HTTPS_REQUEST_FAILED|FTT|3|REQUEST_STARTED",
            text);
        AssertCompactFieldDiagnostic(text);
    }

    [Fact]
    public void FieldDiagnostic_ManyLongStagesStayWithinOnePhotoContract()
    {
        var steps = new SetupStepRecorder();
        for (var index = 0; index < 20; index++)
        {
            steps.Add(new SetupStepResult(
                "PACKAGE_VALID",
                "package",
                SetupStepState.Succeeded,
                "safe"));
        }

        steps.Add(new SetupStepResult(
            SetupErrorCodes.RollbackFailedDirectoryCleanupFailed,
            "cleanup",
            SetupStepState.Failed,
            "safe"));
        steps.Add(new SetupStepResult(
            SetupErrorCodes.RollbackLegacyFirewallRestoreFailed,
            "rollback",
            SetupStepState.Warning,
            "safe"));
        var result = SetupOperationResult.Failure(
            SetupErrorCodes.RollbackFailed,
            "safe",
            steps) with
        {
            PrimaryFailureCode =
                SetupErrorCodes.RollbackFailedDirectoryCleanupFailed
        };

        var text = SetupFieldDiagnosticFormatter.Format(
            new SetupFieldDiagnosticContext(
                "0.10.14-poc",
                DateTimeOffset.UnixEpoch,
                "10.0.26100.0",
                "X64",
                "install",
                TimeSpan.Zero,
                result,
                PendingRecoveryInspection.None));
        var stageLine = text.Split("\r\n", StringSplitOptions.None)[11];

        AssertCompactFieldDiagnostic(text);
        Assert.StartsWith("Stages=22|", stageLine);
        Assert.Contains(
            $"{SetupErrorCodes.RollbackLegacyFirewallRestoreFailed}:W",
            stageLine);
        Assert.DoesNotContain(
            $"{SetupErrorCodes.RollbackFailedDirectoryCleanupFailed}:F",
            stageLine);
        Assert.Contains(
            $"Failure={SetupErrorCodes.RollbackFailedDirectoryCleanupFailed}|CLASSIFIED|unknown",
            text);
    }

    [Fact]
    public void FieldDiagnosticWriter_AtomicallyOverwritesUtf8BomTextAndCleansTemp()
    {
        using var folder = new TemporaryFolder();
        var path = folder.Combine("diagnostic.txt");
        const string original =
            "SSW_FIELD_DIAGNOSTIC/1\r\nComponent=AGENT_SETUP";
        const string replacement =
            "SSW_FIELD_DIAGNOSTIC/1\r\nComponent=AGENT_SETUP\r\nResult=SUCCESS";

        SetupFieldDiagnosticWriter.Write(path, original);
        SetupFieldDiagnosticWriter.Write(path, replacement);

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 3);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
        Assert.Equal(replacement, File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(
            folder.Path,
            "*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void FieldDiagnosticWriter_FailedEncodingPreservesExistingFileAndCleansTemp()
    {
        using var folder = new TemporaryFolder();
        var path = folder.Combine("diagnostic.txt");
        const string original =
            "SSW_FIELD_DIAGNOSTIC/1\r\nComponent=AGENT_SETUP";

        SetupFieldDiagnosticWriter.Write(path, original);

        Assert.Throws<System.Text.EncoderFallbackException>(
            () => SetupFieldDiagnosticWriter.Write(
                path,
                "SSW_FIELD_DIAGNOSTIC/1\uD800"));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(
            folder.Path,
            "*.tmp",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public void FieldDiagnosticWriter_ReportsWriteFailuresToCaller()
    {
        using var folder = new TemporaryFolder();
        var path = folder.Combine("missing", "diagnostic.txt");

        Assert.Throws<DirectoryNotFoundException>(
            () => SetupFieldDiagnosticWriter.Write(
                path,
                "SSW_FIELD_DIAGNOSTIC/1"));
        Assert.Equal(
            "DIAGNOSTIC_WRITE_FAILED",
            SetupErrorCodes.DiagnosticWriteFailed);
    }

    [Fact]
    public void FieldDiagnosticSaveCoordinator_ConvertsDialogFailureToStableCode()
    {
        var writeCalled = false;

        var result = SetupFieldDiagnosticSaveCoordinator.Save(
            selectPath: () => throw new InvalidOperationException(
                "simulated shell dialog failure"),
            createContents: () => "contents",
            write: (_, _) => writeCalled = true);

        Assert.Equal(SetupFieldDiagnosticSaveState.Failed, result.State);
        Assert.Equal(
            SetupErrorCodes.DiagnosticWriteFailed,
            result.ErrorCode);
        Assert.False(writeCalled);
    }

    [Fact]
    public void FieldDiagnosticSaveCoordinator_ConvertsWriterFailureToStableCode()
    {
        var result = SetupFieldDiagnosticSaveCoordinator.Save(
            selectPath: () => "diagnostic.txt",
            createContents: () => "contents",
            write: (_, _) => throw new UnauthorizedAccessException(
                "simulated EDR denial"));

        Assert.Equal(SetupFieldDiagnosticSaveState.Failed, result.State);
        Assert.Equal(
            SetupErrorCodes.DiagnosticWriteFailed,
            result.ErrorCode);
    }

    [Fact]
    public void FieldDiagnosticSaveCoordinator_CancelDoesNotCreateOrWrite()
    {
        var contentCalled = false;
        var writeCalled = false;

        var result = SetupFieldDiagnosticSaveCoordinator.Save(
            selectPath: () => null,
            createContents: () =>
            {
                contentCalled = true;
                return "contents";
            },
            write: (_, _) => writeCalled = true);

        Assert.Equal(SetupFieldDiagnosticSaveState.Cancelled, result.State);
        Assert.Equal(SetupErrorCodes.Ok, result.ErrorCode);
        Assert.False(contentCalled);
        Assert.False(writeCalled);
    }

    private static void AssertCompactFieldDiagnostic(string text)
    {
        var withoutCrlf = text.Replace("\r\n", string.Empty);
        Assert.DoesNotContain('\r', withoutCrlf);
        Assert.DoesNotContain('\n', withoutCrlf);

        var lines = text.Split("\r\n", StringSplitOptions.None);
        Assert.Equal(12, lines.Length);
        Assert.Equal("SSW_FIELD_DIAGNOSTIC/2", lines[0]);
        Assert.StartsWith("Component=", lines[1]);
        Assert.StartsWith("ProductVersion=", lines[2]);
        Assert.StartsWith("Environment=", lines[3]);
        Assert.StartsWith("Run=", lines[4]);
        Assert.StartsWith("FailedStage=", lines[5]);
        Assert.StartsWith("ErrorCode=", lines[6]);
        Assert.StartsWith("Failure=", lines[7]);
        Assert.StartsWith("Action=", lines[8]);
        Assert.StartsWith("State=", lines[9]);
        Assert.StartsWith("Health=", lines[10]);
        Assert.StartsWith("Stages=", lines[11]);
        Assert.All(lines, line =>
        {
            Assert.NotEmpty(line);
            Assert.True(
                line.Length <= 88,
                $"Diagnostic line exceeds 88 characters: {line}");
        });
    }
}
