using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SamsungSwitchWatch.Agent.Setup.Deployment;

namespace SamsungSwitchWatch.Agent.Setup;

public partial class MainWindow : Window
{
    private readonly SetupDiagnosticsService _diagnostics;
    private readonly AgentDeploymentOrchestrator _deployment;
    private readonly bool _diagnosticsOnly;
    private readonly ObservableCollection<ResultRow> _results = [];
    private CancellationTokenSource? _operationCancellation;
    private PendingRecoveryInspection _recoveryInspection =
        PendingRecoveryInspection.None;
    private SetupOperationResult? _lastFailedOperation;
    private SetupOperationResult? _lastCompletedOperation;
    private string _lastOperationName = "none";
    private string _lastCompletedOperationName = "none";
    private TimeSpan _lastCompletedOperationDuration;
    private bool _isBusy;
    private bool _closeRequested;

    public MainWindow(
        SetupDiagnosticsService diagnostics,
        AgentDeploymentOrchestrator deployment,
        bool diagnosticsOnly)
    {
        _diagnostics = diagnostics;
        _deployment = deployment;
        _diagnosticsOnly = diagnosticsOnly;
        InitializeComponent();
        ResultItemsControl.ItemsSource = _results;

        if (_diagnosticsOnly)
        {
            Title = "Samsung Switch Watch Agent 진단";
            ModeDescription.Text = "읽기 전용 진단 모드입니다. 서비스와 방화벽 설정을 변경하지 않습니다.";
            InstallButton.IsEnabled = false;
            InstallButton.ToolTip = "진단 모드에서는 설치를 실행하지 않습니다.";
            CheckButton.Visibility = Visibility.Visible;
        }

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshRecoveryState(
            preserveFailureDiagnostics: _lastFailedOperation is not null);
    }

    private void RefreshRecoveryState(bool preserveFailureDiagnostics)
    {
        PendingRecoveryInspection inspection;
        try
        {
            inspection = _deployment.InspectPendingRecovery();
        }
        catch
        {
            inspection = new PendingRecoveryInspection(
                Exists: true,
                CanRecover: false,
                SetupErrorCodes.Unexpected,
                "이전 설치 상태를 확인하지 못했습니다. 설치 파일을 삭제하거나 다시 실행하지 말고 관리자에게 문의하세요.");
        }

        _recoveryInspection = inspection;
        ApplyRecoveryState(inspection);

        if (inspection.Exists)
        {
            var preserveExistingFailure =
                preserveFailureDiagnostics &&
                _lastFailedOperation is { Succeeded: false };
            var pendingResult =
                SetupResultPresentation.BuildPendingRecoveryResult(inspection);

            if (!preserveExistingFailure)
            {
                _lastFailedOperation = pendingResult;
                _lastOperationName = "recovery-inspection";
            }

            if (!inspection.CanRecover || preserveExistingFailure)
            {
                ShowDiagnosticsAction();
            }
            else
            {
                ClearDiagnosticsAction();
            }

            if (!preserveExistingFailure)
            {
                ShowResultSteps(pendingResult);
                OperationStateText.Text = inspection.CanRecover
                    ? "복구 필요"
                    : $"확인 필요 · {inspection.Code}";
                OperationStateText.Foreground = inspection.CanRecover
                    ? Brushes.DarkGoldenrod
                    : Brushes.Firebrick;
            }
        }
        else if (!preserveFailureDiagnostics)
        {
            ClearDiagnosticsAction();
        }

        UpdateActionAvailability();
        RefreshSupportCode();
    }

    private void ApplyRecoveryState(PendingRecoveryInspection inspection)
    {
        if (!inspection.Exists)
        {
            RecoveryStatusBorder.Visibility = Visibility.Collapsed;
            RecoverButton.Visibility = Visibility.Collapsed;
            RecoverButton.Content = "이전 상태 복구";
            ActionGuidanceText.Text =
                "설정 변경은 설치 버튼을 누른 뒤에만 수행됩니다.";
            return;
        }

        RecoveryStatusBorder.Visibility = Visibility.Visible;
        RecoverButton.Visibility = Visibility.Visible;
        RecoverButton.Content = "이전 상태 복구";
        if (inspection.CanRecover)
        {
            RecoveryStatusBorder.Background =
                new SolidColorBrush(Color.FromRgb(255, 251, 235));
            RecoveryStatusBorder.BorderBrush =
                new SolidColorBrush(Color.FromRgb(245, 158, 11));
            RecoveryStatusTitle.Foreground = Brushes.DarkGoldenrod;
            RecoveryStatusTitle.Text = "이전 설치 상태를 먼저 복구하세요";
            RecoveryStatusText.Text =
                $"{inspection.Message}\n복구 완료 후 설치/업데이트는 자동으로 시작되지 않습니다.";
            ActionGuidanceText.Text =
                "1) 이전 상태 복구  2) 복구 완료 확인  3) 설치 / 업데이트";
        }
        else
        {
            RecoveryStatusBorder.Background =
                new SolidColorBrush(Color.FromRgb(254, 242, 242));
            RecoveryStatusBorder.BorderBrush =
                new SolidColorBrush(Color.FromRgb(220, 38, 38));
            RecoveryStatusTitle.Foreground = Brushes.Firebrick;
            RecoveryStatusTitle.Text = "이전 상태 복구를 진행할 수 없습니다";
            RecoveryStatusText.Text =
                $"{inspection.Message}\n코드: {inspection.Code}\n설치 파일을 삭제하지 말고 관리자에게 이 코드를 전달하세요.";
            ActionGuidanceText.Text =
                "복구와 설치가 잠겼습니다. 진단정보를 복사해 관리자에게 전달하세요.";
        }
    }

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        var request = CreateRequest();
        var result = await RunOperationAsync(
            "preflight",
            "사전 점검 중",
            cancellationToken => _diagnostics.RunAsync(request, cancellationToken));
        RefreshRecoveryState(
            preserveFailureDiagnostics: result is { Succeeded: false });
    }

    private async void RecoverButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_recoveryInspection.Exists || !_recoveryInspection.CanRecover)
        {
            RefreshRecoveryState(preserveFailureDiagnostics: true);
            return;
        }

        var result = await RunOperationAsync(
            "recovery",
            "이전 상태 복구 중",
            cancellationToken => _deployment.RecoverAsync(cancellationToken));
        RefreshRecoveryState(
            preserveFailureDiagnostics: result is { Succeeded: false });
        if (result is not null)
        {
            ApplyRecoveryCompletion(result);
        }
    }

    private void ApplyRecoveryCompletion(SetupOperationResult result)
    {
        var completion = SetupRecoveryCompletionPolicy.Evaluate(
            result,
            _recoveryInspection);
        var displayedResult = completion.UseInspectionResult
            ? SetupResultPresentation.BuildPendingRecoveryResult(
                _recoveryInspection)
            : result;

        ShowResultSteps(displayedResult);
        OperationStateText.Text = completion.StatusText;
        OperationStateText.Foreground = completion.Severity switch
        {
            SetupRecoveryCompletionSeverity.Success => Brushes.SeaGreen,
            SetupRecoveryCompletionSeverity.Warning => Brushes.DarkGoldenrod,
            _ => Brushes.Firebrick
        };
        ActionGuidanceText.Text = completion.GuidanceText;

        if (completion.ReadyForInstall)
        {
            ClearDiagnosticsAction();
        }
        else
        {
            _lastFailedOperation = displayedResult;
            _lastOperationName = completion.UseInspectionResult
                ? "recovery-inspection"
                : "recovery";
            ShowDiagnosticsAction();
            RecoverButton.Content = "복구 다시 시도";
            if (!result.Succeeded)
            {
                RecoveryStatusBorder.Visibility = Visibility.Visible;
                RecoveryStatusBorder.Background =
                    new SolidColorBrush(Color.FromRgb(254, 242, 242));
                RecoveryStatusBorder.BorderBrush =
                    new SolidColorBrush(Color.FromRgb(220, 38, 38));
                RecoveryStatusTitle.Foreground = Brushes.Firebrick;
                RecoveryStatusTitle.Text =
                    "이전 상태를 완전히 복구하지 못했습니다";
                RecoveryStatusText.Text =
                    "설치 자료 정리 미완료 · 작업 기록 보존\n" +
                    "설치는 계속 잠겨 있습니다. 복구 다시 시도를 누르고, 반복되면 익명 진단을 저장하세요.";
                ActionGuidanceText.Text =
                    "복구 다시 시도를 먼저 누르고, 반복되면 익명 진단을 저장하거나 진단정보를 복사해 관리자에게 전달하세요.";
            }
        }

        CaptureCompletedOperation(
            "recovery",
            displayedResult,
            _lastCompletedOperationDuration);
        RefreshSupportCode();
        UpdateActionAvailability();
        InstallButton.IsEnabled =
            completion.ReadyForInstall && InstallButton.IsEnabled;
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRecoveryState(preserveFailureDiagnostics: true);
        if (_recoveryInspection.Exists)
        {
            return;
        }

        var request = CreateRequest();
        var confirmation = MessageBox.Show(
            this,
            "창 없이 실행되는 Agent 서비스를 설치하거나 업데이트합니다.\n" +
            "사설 Viewer 대역과 사설 스위치 관리망 범위는 자동으로 적용됩니다.\n\n" +
            "계속하시겠습니까?",
            "Agent 설치 확인",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        var result = await RunOperationAsync(
            "install",
            "설치 전 점검 중",
            async cancellationToken =>
            {
                var preflight = await _diagnostics.RunAsync(request, cancellationToken);
                if (!preflight.Succeeded)
                {
                    return preflight;
                }

                return await _deployment.DeployAsync(request, cancellationToken);
            });
        RefreshRecoveryState(
            preserveFailureDiagnostics: result is { Succeeded: false });
        if (result is not null && !_recoveryInspection.Exists)
        {
            ApplyInstallCompletion(result);
        }
    }

    private void ApplyInstallCompletion(SetupOperationResult result)
    {
        var completion = SetupInstallCompletionPolicy.Evaluate(result);
        OperationStateText.Text = completion.StatusText;
        OperationStateText.Foreground = completion.Severity switch
        {
            SetupInstallCompletionSeverity.Success => Brushes.SeaGreen,
            SetupInstallCompletionSeverity.Warning => Brushes.DarkGoldenrod,
            _ => Brushes.Firebrick
        };
        ActionGuidanceText.Text = completion.GuidanceText;
    }

    private SetupRequest CreateRequest() =>
        SetupConstants.CreateAutomaticRequest();

    private async Task<SetupOperationResult?> RunOperationAsync(
        string operationName,
        string runningText,
        Func<CancellationToken, Task<SetupOperationResult>> operation)
    {
        var stopwatch = Stopwatch.StartNew();
        HideSupportCode();
        SetBusy(true);
        _results.Clear();
        DiagnosticsCopyFeedbackText.Visibility = Visibility.Collapsed;
        OperationStateText.Text = runningText;
        _operationCancellation = new CancellationTokenSource();
        try
        {
            var result = await Task.Run(
                () => operation(_operationCancellation.Token),
                _operationCancellation.Token);
            ShowResultSteps(result);

            var warningCount = result.Steps.Count(step =>
                step.State == SetupStepState.Warning);
            OperationStateText.Text = result.Succeeded
                ? warningCount == 0
                    ? "완료"
                    : $"경고 {warningCount}건 · 완료"
                : $"실패 · {result.Code}";
            OperationStateText.Foreground = result.Succeeded
                ? warningCount == 0
                    ? Brushes.SeaGreen
                    : Brushes.DarkGoldenrod
                : Brushes.Firebrick;

            if (result.Succeeded)
            {
                ClearDiagnosticsAction();
            }
            else
            {
                _lastFailedOperation = result;
                _lastOperationName = operationName;
                ShowDiagnosticsAction();
            }

            CaptureCompletedOperation(
                operationName,
                result,
                stopwatch.Elapsed);
            return result;
        }
        catch
        {
            ShowSingleFailure(
                SetupErrorCodes.Unexpected,
                "작업 실패",
                "화면에서 작업 결과를 처리하지 못했습니다.",
                operationName);
            if (_lastFailedOperation is { } failure)
            {
                CaptureCompletedOperation(
                    operationName,
                    failure,
                    stopwatch.Elapsed);
            }
            return _lastFailedOperation;
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            SetBusy(false);
            if (_closeRequested)
            {
                _ = Dispatcher.BeginInvoke(Close);
            }
        }
    }

    private void ShowResultSteps(SetupOperationResult result)
    {
        _results.Clear();
        foreach (var step in SetupResultPresentation.BuildSteps(result))
        {
            _results.Add(ResultRow.From(step));
        }
    }

    private void ShowSingleFailure(
        string code,
        string label,
        string message,
        string operationName = "ui")
    {
        var result = SetupOperationResult.Failure(
            code,
            message,
            [new SetupStepResult(code, label, SetupStepState.Failed, message)]);
        ShowResultSteps(result);
        _lastFailedOperation = result;
        _lastOperationName = operationName;
        ShowDiagnosticsAction();
        OperationStateText.Text = $"실패 · {code}";
        OperationStateText.Foreground = Brushes.Firebrick;
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        CheckButton.IsEnabled = !busy;
        CopyDiagnosticsButton.IsEnabled = !busy;
        SaveFieldDiagnosticButton.IsEnabled =
            !busy && _lastCompletedOperation is not null;
        UpdateActionAvailability();
    }

    private void UpdateActionAvailability()
    {
        var state = SetupRecoveryActionPolicy.Evaluate(
            _diagnosticsOnly,
            _isBusy,
            _recoveryInspection);
        InstallButton.IsEnabled = state.InstallEnabled;
        RecoverButton.Visibility =
            state.RecoverVisible ? Visibility.Visible : Visibility.Collapsed;
        RecoverButton.IsEnabled = state.RecoverEnabled;
    }

    private void ShowDiagnosticsAction()
    {
        CopyDiagnosticsButton.Visibility = Visibility.Visible;
        DiagnosticsCopyFeedbackText.Visibility = Visibility.Collapsed;
    }

    private void ClearDiagnosticsAction()
    {
        _lastFailedOperation = null;
        _lastOperationName = "none";
        CopyDiagnosticsButton.Visibility = Visibility.Collapsed;
        DiagnosticsCopyFeedbackText.Visibility = Visibility.Collapsed;
        HideSupportCode();
    }

    private void RefreshSupportCode()
    {
        HideSupportCode();
        if (_lastFailedOperation is not { Succeeded: false } failure)
        {
            return;
        }

        try
        {
            SupportCodeTextBox.Text =
                SetupFieldDiagnosticFormatter.CreateSupportCode(
                    new SetupFieldDiagnosticContext(
                        ProductVersion(),
                        DateTimeOffset.UtcNow,
                        Environment.OSVersion.Version.ToString(),
                        RuntimeInformation.OSArchitecture.ToString(),
                        _lastOperationName,
                        _lastCompletedOperationDuration,
                        failure,
                        _recoveryInspection));
            SupportCodeBorder.Visibility = Visibility.Visible;
        }
        catch
        {
            HideSupportCode();
        }
    }

    private void HideSupportCode()
    {
        SupportCodeTextBox.Text = string.Empty;
        SupportCodeBorder.Visibility = Visibility.Collapsed;
    }

    private void CopyDiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFailedOperation is null || _lastFailedOperation.Succeeded)
        {
            return;
        }

        try
        {
            Clipboard.SetText(SetupFailureDiagnosticFormatter.Format(
                new SetupFailureDiagnosticContext(
                    ProductVersion(),
                    DateTimeOffset.UtcNow,
                    _lastOperationName,
                    _lastFailedOperation,
                    _recoveryInspection)));
            DiagnosticsCopyFeedbackText.Text =
                "민감정보를 제외한 진단정보를 복사했습니다.";
            DiagnosticsCopyFeedbackText.Foreground = Brushes.SeaGreen;
            DiagnosticsCopyFeedbackText.Visibility = Visibility.Visible;
        }
        catch
        {
            DiagnosticsCopyFeedbackText.Text =
                "클립보드에 복사하지 못했습니다. 잠시 후 다시 시도하세요.";
            DiagnosticsCopyFeedbackText.Foreground = Brushes.Firebrick;
            DiagnosticsCopyFeedbackText.Visibility = Visibility.Visible;
        }
    }

    private void CaptureCompletedOperation(
        string operationName,
        SetupOperationResult result,
        TimeSpan duration)
    {
        _lastCompletedOperation = result;
        _lastCompletedOperationName = operationName;
        _lastCompletedOperationDuration = duration;
        SaveFieldDiagnosticButton.Visibility = Visibility.Visible;
        SaveFieldDiagnosticButton.IsEnabled = !_isBusy;
    }

    private void SaveFieldDiagnosticButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_lastCompletedOperation is not { } completedOperation)
        {
            return;
        }

        var generatedUtc = DateTimeOffset.UtcNow;
        var saveResult = SetupFieldDiagnosticSaveCoordinator.Save(
            selectPath: () =>
            {
                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "익명 현장 진단 저장",
                    FileName =
                        $"SSW-AgentSetup-Diagnostic-{generatedUtc:yyyyMMdd-HHmmss}.txt",
                    DefaultExt = ".txt",
                    AddExtension = true,
                    OverwritePrompt = true,
                    Filter = "텍스트 파일 (*.txt)|*.txt"
                };
                return dialog.ShowDialog(this) == true
                    ? dialog.FileName
                    : null;
            },
            createContents: () => SetupFieldDiagnosticFormatter.Format(
                new SetupFieldDiagnosticContext(
                    ProductVersion(),
                    generatedUtc,
                    Environment.OSVersion.Version.ToString(),
                    RuntimeInformation.OSArchitecture.ToString(),
                    _lastCompletedOperationName,
                    _lastCompletedOperationDuration,
                    completedOperation,
                    _recoveryInspection)),
            write: SetupFieldDiagnosticWriter.Write);

        if (saveResult.State == SetupFieldDiagnosticSaveState.Cancelled)
        {
            return;
        }

        if (saveResult.State == SetupFieldDiagnosticSaveState.Succeeded)
        {
            DiagnosticsCopyFeedbackText.Text =
                "사진 한 장용 익명 진단 TXT를 저장했습니다.";
            DiagnosticsCopyFeedbackText.Foreground = Brushes.SeaGreen;
            DiagnosticsCopyFeedbackText.Visibility = Visibility.Visible;
            return;
        }

        DiagnosticsCopyFeedbackText.Text =
            $"저장 실패 · {saveResult.ErrorCode}\n" +
            "쓰기 가능한 다른 폴더를 선택한 뒤 다시 시도하세요.";
        DiagnosticsCopyFeedbackText.Foreground = Brushes.Firebrick;
        DiagnosticsCopyFeedbackText.Visibility = Visibility.Visible;
    }

    private static string ProductVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        return assembly
                   .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
                   .Split('+', 2)[0] ??
               assembly.GetName().Version?.ToString() ??
               "unknown";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_operationCancellation is null)
        {
            return;
        }

        e.Cancel = true;
        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;
        OperationStateText.Text = "취소 요청 처리 중";
        OperationStateText.Foreground = Brushes.DarkGoldenrod;
        _operationCancellation.Cancel();
    }
}

public sealed class NetworkSelectionItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public NetworkSelectionItem(NetworkCandidate candidate)
        : this(
            candidate.InterfaceName,
            candidate.Address,
            candidate.Cidr,
            candidate.Description,
            $"이 PC 주소: {candidate.Address} · {candidate.Description}",
            $"{candidate.InterfaceName} · {candidate.Cidr} · 이 PC {candidate.Address}",
            canRemove: false)
    {
    }

    private NetworkSelectionItem(
        string interfaceName,
        string address,
        string cidr,
        string description,
        string detailText,
        string displayText,
        bool canRemove)
    {
        InterfaceName = interfaceName;
        Address = address;
        Cidr = cidr;
        Description = description;
        DetailText = detailText;
        DisplayText = displayText;
        CanRemove = canRemove;
    }

    public string InterfaceName { get; }
    public string Address { get; }
    public string Cidr { get; }
    public string Description { get; }
    public string DetailText { get; }
    public string DisplayText { get; }
    public bool CanRemove { get; }

    internal static NetworkSelectionItem FromManualInput(string cidr) =>
        new(
            "직접 추가",
            "-",
            cidr,
            "수동 입력",
            "수동 입력",
            $"직접 추가 · {cidr} · 수동 입력",
            canRemove: true);

    internal static NetworkSelectionItem FromSavedConfiguration(string cidr) =>
        new(
            "기존 설정",
            "-",
            cidr,
            "직접 연결 아님",
            "직접 연결 아님",
            $"기존 설정 · {cidr} · 직접 연결 아님",
            canRemove: true);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ResultRow(
    string Symbol,
    Brush Brush,
    string Label,
    string Message,
    string Code)
{
    public static ResultRow From(SetupStepResult step) =>
        new(
            step.State switch
            {
                SetupStepState.Succeeded => "●",
                SetupStepState.Failed => "✕",
                SetupStepState.Running => "…",
                SetupStepState.Warning => "▲",
                _ => "●"
            },
            step.State switch
            {
                SetupStepState.Succeeded => Brushes.SeaGreen,
                SetupStepState.Failed => Brushes.Firebrick,
                SetupStepState.Running => Brushes.RoyalBlue,
                SetupStepState.Warning => Brushes.DarkGoldenrod,
                _ => Brushes.DarkGoldenrod
            },
            step.Label,
            step.Message,
            step.Code);
}
