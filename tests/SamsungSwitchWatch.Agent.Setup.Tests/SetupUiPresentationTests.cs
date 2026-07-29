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

        Assert.StartsWith("SSW_FIELD_DIAGNOSTIC/1\r\n", text);
        Assert.Contains("Component=AGENT_SETUP", text);
        Assert.Contains("ProductVersion=0.10.8-poc", text);
        Assert.Contains("GeneratedUtc=20260729T010203000Z", text);
        Assert.Contains("WindowsBuild=WIN_10_0_26100_0", text);
        Assert.Contains("Architecture=X64", text);
        Assert.Contains("Operation=INSTALL", text);
        Assert.Contains("Result=FAILURE", text);
        Assert.Contains("FailedStage=FIREWALL", text);
        Assert.Contains(
            $"ErrorCode={SetupErrorCodes.FirewallFailed}",
            text);
        Assert.Contains(
            "RecommendedActionCode=CHECK_FIREWALL_POLICY",
            text);
        Assert.Contains("OperationDurationMs=321", text);
        Assert.Contains("PackageValidation=PASS", text);
        Assert.Contains("RecoveryJournal=PENDING_RECOVERABLE", text);
        Assert.Contains("Service=CONFIGURED", text);
        Assert.Contains(
            FirewallRuleMismatchCodes.RemoteAddress,
            text);
        Assert.Contains("LocalTcp18443=NOT_RUN", text);
        Assert.Contains("Readiness=NOT_RUN", text);
        Assert.Contains("StageCount=3", text);
        Assert.Contains("Stage.01.Code=PACKAGE_VALID", text);
        Assert.Contains("Stage.01.Status=SUCCESS", text);
        Assert.Matches(@"Stage\.01\.DurationMs=\d+", text);
        Assert.Matches(@"Stage\.03\.ElapsedMs=\d+", text);
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
        var result = SetupOperationResult.Success("done", steps);

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

        Assert.Contains("Operation=PREFLIGHT", text);
        Assert.Contains("Result=SUCCESS", text);
        Assert.Contains("FailedStage=NONE", text);
        Assert.Contains("ErrorCode=OK", text);
        Assert.Contains("RecommendedActionCode=NONE", text);
        Assert.Contains("PackageValidation=PASS", text);
        Assert.Contains("RecoveryJournal=NONE", text);
        Assert.Contains("Service=RUNNING_READY", text);
        Assert.Contains("LocalTcp18443=PASS", text);
        Assert.Contains("Readiness=PASS", text);
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
        Assert.Contains("WindowsBuild=UNAVAILABLE", text);
        Assert.Contains("Architecture=UNAVAILABLE", text);
        Assert.Contains("Operation=UNAVAILABLE", text);
        Assert.Contains("FailedStage=UNKNOWN", text);
        Assert.Contains("ErrorCode=UNAVAILABLE", text);
        Assert.Contains("OperationDurationMs=unknown", text);
        Assert.Contains("Stage.01.Code=UNAVAILABLE", text);
        Assert.DoesNotContain(sensitive, text);
        Assert.DoesNotContain(@"C:\", text);
        Assert.DoesNotContain("DOMAIN", text);
        Assert.DoesNotContain("operator", text);
        Assert.DoesNotContain("password", text);
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
}
