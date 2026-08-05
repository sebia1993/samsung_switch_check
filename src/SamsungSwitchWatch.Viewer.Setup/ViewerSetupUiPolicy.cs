using SamsungSwitchWatch.Viewer.Setup.Deployment;

namespace SamsungSwitchWatch.Viewer.Setup;

public sealed record ViewerSetupButtonState(
    bool InstallEnabled,
    bool RecoverEnabled,
    bool CloseEnabled);

public enum ViewerSetupOperationKind
{
    Install,
    Recovery
}

public sealed record ViewerSetupOperationPresentation(
    string Title,
    string Message);

public static class ViewerSetupUiPolicy
{
    public const string SuccessMessage =
        "설치 완료 · Viewer 실행됨 · 압축 해제한 임시 폴더는 삭제할 수 있습니다.";

    public const string RecoverySuccessMessage =
        "복구 완료 · 설치 / 업데이트를 별도로 실행하세요.";

    public static ViewerSetupButtonState Buttons(
        bool busy,
        ViewerRecoveryInspection recovery) =>
        new(
            InstallEnabled: !busy && !recovery.Exists,
            RecoverEnabled: !busy && recovery.Exists && recovery.CanRecover,
            CloseEnabled: !busy);

    public static ViewerSetupOperationPresentation Result(
        ViewerSetupOperationKind operation,
        ViewerSetupResult result) =>
        operation switch
        {
            ViewerSetupOperationKind.Recovery when result.Succeeded =>
                new("복구 완료", RecoverySuccessMessage),
            ViewerSetupOperationKind.Recovery =>
                new("복구 실패", result.Message),
            ViewerSetupOperationKind.Install when result.Succeeded =>
                new("완료", SuccessMessage),
            _ => new("설치 실패", result.Message)
        };
}
