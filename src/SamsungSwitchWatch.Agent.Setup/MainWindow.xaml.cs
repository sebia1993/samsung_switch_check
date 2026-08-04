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
    private readonly INetworkDiscovery _networkDiscovery;
    private readonly SetupDiagnosticsService _diagnostics;
    private readonly AgentDeploymentOrchestrator _deployment;
    private readonly bool _diagnosticsOnly;
    private readonly ObservableCollection<NetworkSelectionItem> _networks = [];
    private readonly ObservableCollection<ResultRow> _results = [];
    private CancellationTokenSource? _operationCancellation;
    private IReadOnlyList<string> _initialTargetCidrs = [];
    private SetupStepResult? _existingNetworksWarning;
    private PendingRecoveryInspection _recoveryInspection =
        PendingRecoveryInspection.None;
    private SetupOperationResult? _lastFailedOperation;
    private SetupOperationResult? _lastCompletedOperation;
    private string _lastOperationName = "none";
    private string _lastCompletedOperationName = "none";
    private TimeSpan _lastCompletedOperationDuration;
    private bool _suppressNetworkSelectionEvent;
    private bool _initialNetworksApplied;
    private bool _isBusy;
    private bool _closeRequested;

    public MainWindow(
        INetworkDiscovery networkDiscovery,
        SetupDiagnosticsService diagnostics,
        AgentDeploymentOrchestrator deployment,
        bool diagnosticsOnly)
    {
        _networkDiscovery = networkDiscovery;
        _diagnostics = diagnostics;
        _deployment = deployment;
        _diagnosticsOnly = diagnosticsOnly;
        InitializeComponent();
        NetworkItemsControl.ItemsSource = _networks;
        ResultItemsControl.ItemsSource = _results;

        if (_diagnosticsOnly)
        {
            Title = "Samsung Switch Watch Agent 진단";
            ModeDescription.Text = "읽기 전용 진단 모드입니다. 서비스와 방화벽 설정을 변경하지 않습니다.";
            InstallButton.IsEnabled = false;
            InstallButton.ToolTip = "진단 모드에서는 설치를 실행하지 않습니다.";
        }

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshNetworks();
        RefreshRecoveryState(
            preserveFailureDiagnostics: _lastFailedOperation is not null);
    }

    internal void InitializeExistingTargetNetworks(
        IReadOnlyList<string> targetCidrs,
        SetupStepResult? warning)
    {
        if (IsLoaded)
        {
            throw new InvalidOperationException(
                "Existing management networks must be initialized before the window is shown.");
        }

        _initialTargetCidrs = targetCidrs.ToArray();
        _existingNetworksWarning = warning;
    }

    private void RefreshNetworksButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshNetworks();
        RefreshRecoveryState(
            preserveFailureDiagnostics:
                _lastFailedOperation is { Succeeded: false });
    }

    private void ViewerIpTextBox_TextChanged(
        object sender,
        System.Windows.Controls.TextChangedEventArgs e)
    {
        if (IsInitialized)
        {
            HideSupportCode();
        }
    }

    private void UseThisPcAddressButton_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<NetworkCandidate> candidates;
        try
        {
            candidates = _networkDiscovery.DiscoverPrivateIpv4Networks();
        }
        catch
        {
            HideViewerAddressChoices();
            ShowViewerAddressFeedback(
                "이 PC의 네트워크 정보를 읽지 못했습니다. 활성 어댑터를 확인한 뒤 Viewer PC의 고정 사설 IPv4를 직접 입력하세요.",
                Brushes.Firebrick);
            return;
        }

        var suggestion = ViewerAddressSuggestion.Create(candidates);
        switch (suggestion.Kind)
        {
            case ViewerAddressSuggestionKind.None:
                HideViewerAddressChoices();
                ShowViewerAddressFeedback(
                    "사용할 수 있는 사설 IPv4를 찾지 못했습니다. 유선 또는 무선 어댑터 연결을 확인한 뒤 Viewer PC의 고정 사설 IPv4를 직접 입력하세요.",
                    Brushes.Firebrick);
                break;
            case ViewerAddressSuggestionKind.Single:
                HideViewerAddressChoices();
                ApplyThisPcViewerAddress(suggestion.Choices[0]);
                break;
            case ViewerAddressSuggestionKind.Multiple:
                ViewerAddressCandidatesComboBox.ItemsSource = suggestion.Choices;
                ViewerAddressCandidatesComboBox.SelectedIndex = -1;
                ViewerAddressCandidatesComboBox.Visibility = Visibility.Visible;
                ShowViewerAddressFeedback(
                    "사설 IPv4가 여러 개입니다. Viewer 연결에 사용할 어댑터 주소를 아래에서 선택하세요.",
                    Brushes.DarkGoldenrod);
                ViewerAddressCandidatesComboBox.Focus();
                break;
        }
    }

    private void ViewerAddressCandidatesComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ViewerAddressCandidatesComboBox.SelectedItem is ViewerAddressChoice choice)
        {
            ApplyThisPcViewerAddress(choice);
        }
    }

    private void ApplyThisPcViewerAddress(ViewerAddressChoice choice)
    {
        ViewerIpTextBox.Text = choice.Address;
        ShowViewerAddressFeedback(
            $"이 PC 주소 {choice.Address}를 입력했습니다. 동일 PC 사전 테스트 후 원격 배치 전 실제 Viewer PC의 고정 IPv4로 바꾸세요.",
            Brushes.DarkGoldenrod);
    }

    private void HideViewerAddressChoices()
    {
        ViewerAddressCandidatesComboBox.SelectedIndex = -1;
        ViewerAddressCandidatesComboBox.ItemsSource = null;
        ViewerAddressCandidatesComboBox.Visibility = Visibility.Collapsed;
    }

    private void ShowViewerAddressFeedback(string message, Brush brush)
    {
        ViewerAddressFeedbackText.Text = message;
        ViewerAddressFeedbackText.Foreground = brush;
        ViewerAddressFeedbackText.Visibility = Visibility.Visible;
    }

    private void RefreshNetworks()
    {
        var applyingInitialNetworks = !_initialNetworksApplied;
        var selectedCidrs = _networks
            .Where(item => item.IsSelected)
            .Select(item => item.Cidr)
            .ToHashSet(StringComparer.Ordinal);
        var preservedManualItems = _networks
            .Where(item => item.CanRemove)
            .ToArray();

        IReadOnlyList<NetworkCandidate> candidates;
        try
        {
            candidates = _networkDiscovery.DiscoverPrivateIpv4Networks();
        }
        catch
        {
            candidates = [];
            ShowSingleFailure(
                SetupErrorCodes.Unexpected,
                "관리망 검색",
                "Windows 네트워크 어댑터 정보를 읽지 못했습니다.");
        }

        if (applyingInitialNetworks)
        {
            selectedCidrs.Clear();
            if (_existingNetworksWarning is null)
            {
                selectedCidrs.UnionWith(_initialTargetCidrs);
                if (_initialTargetCidrs.Count == 0)
                {
                    var discoveredCidrs = candidates
                        .Select(candidate => candidate.Cidr)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    if (discoveredCidrs.Length == 1)
                    {
                        selectedCidrs.Add(discoveredCidrs[0]);
                    }
                }
            }

            _initialNetworksApplied = true;
            ShowExistingNetworksStatus();
        }

        _networks.Clear();
        foreach (var candidate in candidates)
        {
            _networks.Add(new NetworkSelectionItem(candidate)
            {
                IsSelected = selectedCidrs.Contains(candidate.Cidr)
            });
        }

        var discovered = candidates
            .Select(candidate => candidate.Cidr)
            .ToHashSet(StringComparer.Ordinal);
        var manualItems = preservedManualItems
            .Where(item => !discovered.Contains(item.Cidr))
            .ToDictionary(item => item.Cidr, StringComparer.Ordinal);
        foreach (var cidr in selectedCidrs)
        {
            if (!discovered.Contains(cidr) && !manualItems.ContainsKey(cidr))
            {
                manualItems.Add(
                    cidr,
                    NetworkSelectionItem.FromSavedConfiguration(cidr));
            }
        }

        foreach (var item in manualItems.Values.OrderBy(item => item.Cidr, StringComparer.Ordinal))
        {
            item.IsSelected = selectedCidrs.Contains(item.Cidr);
            _networks.Add(item);
        }

        NoNetworksText.Visibility =
            candidates.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NetworkSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressNetworkSelectionEvent)
        {
            return;
        }

        if (sender is not FrameworkElement
            {
                DataContext: NetworkSelectionItem selectedItem
            })
        {
            return;
        }

        HideSupportCode();
        var requestedState = selectedItem.IsSelected;
        _suppressNetworkSelectionEvent = true;
        try
        {
            foreach (var item in _networks.Where(item =>
                         string.Equals(
                             item.Cidr,
                             selectedItem.Cidr,
                             StringComparison.Ordinal)))
            {
                item.IsSelected = requestedState;
            }

            if (SelectedCidrCount() > 2)
            {
                foreach (var item in _networks.Where(item =>
                             string.Equals(
                                 item.Cidr,
                                 selectedItem.Cidr,
                                 StringComparison.Ordinal)))
                {
                    item.IsSelected = false;
                }
            }
        }
        finally
        {
            _suppressNetworkSelectionEvent = false;
        }

        if (requestedState && !selectedItem.IsSelected)
        {
            ShowManualNetworkFeedback(
                "자동 선택과 직접 추가를 합해 최대 두 개까지 사용할 수 있습니다. 기존 항목을 먼저 해제하세요.",
                Brushes.DarkGoldenrod);
        }
    }

    private void AddManualNetworkButton_Click(object sender, RoutedEventArgs e) =>
        AddManualNetwork();

    private void ManualCidrTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        AddManualNetwork();
    }

    private void AddManualNetwork()
    {
        if (!Ipv4Input.TryNormalizePrivateCidr(
                ManualCidrTextBox.Text,
                out var canonicalCidr))
        {
            ShowManualNetworkFeedback(
                "입력 오류: RFC1918 사설 IPv4 CIDR과 /0~32 범위를 확인하세요.",
                Brushes.Firebrick);
            return;
        }

        HideSupportCode();
        var matchingItems = _networks
            .Where(item => string.Equals(
                item.Cidr,
                canonicalCidr,
                StringComparison.Ordinal))
            .ToArray();
        if (matchingItems.Length > 0)
        {
            if (matchingItems.Any(item => item.IsSelected))
            {
                ShowManualNetworkFeedback(
                    $"이미 같은 관리망이 선택되어 있습니다: {canonicalCidr}",
                    Brushes.RoyalBlue);
                return;
            }

            if (SelectedCidrCount() >= 2)
            {
                ShowManualNetworkFeedback(
                    "최대 두 개까지 선택할 수 있습니다. 기존 항목을 먼저 해제하세요.",
                    Brushes.DarkGoldenrod);
                return;
            }

            SetCidrSelected(canonicalCidr, true);
            ManualCidrTextBox.Clear();
            ShowManualNetworkFeedback(
                $"기존 항목을 선택했습니다: {canonicalCidr}",
                Brushes.SeaGreen);
            return;
        }

        if (SelectedCidrCount() >= 2)
        {
            ShowManualNetworkFeedback(
                "최대 두 개까지 선택할 수 있습니다. 기존 항목을 먼저 해제하세요.",
                Brushes.DarkGoldenrod);
            return;
        }

        var manualItem = NetworkSelectionItem.FromManualInput(canonicalCidr);
        manualItem.IsSelected = true;
        _networks.Add(manualItem);
        ManualCidrTextBox.Clear();
        ShowManualNetworkFeedback(
            $"추가됨: {canonicalCidr} · 총 2개 중 {SelectedCidrCount()}개 선택",
            Brushes.SeaGreen);
    }

    private void RemoveManualNetworkButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: NetworkSelectionItem { CanRemove: true } item
            })
        {
            return;
        }

        _networks.Remove(item);
        HideSupportCode();
        ShowManualNetworkFeedback(
            $"직접 추가 관리망을 삭제했습니다: {item.Cidr}",
            Brushes.RoyalBlue);
    }

    private int SelectedCidrCount() =>
        _networks
            .Where(item => item.IsSelected)
            .Select(item => item.Cidr)
            .Distinct(StringComparer.Ordinal)
            .Count();

    private void SetCidrSelected(string cidr, bool isSelected)
    {
        _suppressNetworkSelectionEvent = true;
        try
        {
            foreach (var item in _networks.Where(item =>
                         string.Equals(item.Cidr, cidr, StringComparison.Ordinal)))
            {
                item.IsSelected = isSelected;
            }
        }
        finally
        {
            _suppressNetworkSelectionEvent = false;
        }
    }

    private void ShowManualNetworkFeedback(string message, Brush brush)
    {
        ManualNetworkFeedbackText.Text = message;
        ManualNetworkFeedbackText.Foreground = brush;
    }

    private void ShowExistingNetworksStatus()
    {
        if (_existingNetworksWarning is not null)
        {
            ExistingNetworksWarningText.Text =
                $"{_existingNetworksWarning.Code}: {_existingNetworksWarning.Message}";
            ExistingNetworksWarningText.Visibility = Visibility.Visible;
            return;
        }

        ExistingNetworksWarningText.Visibility = Visibility.Collapsed;
        if (_initialTargetCidrs.Count > 0)
        {
            ShowManualNetworkFeedback(
                $"기존 설정의 관리망 {_initialTargetCidrs.Count}개를 불러왔습니다.",
                Brushes.SeaGreen);
        }
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
        var networks = request.TargetCidrs.Count == 0
            ? "(선택 없음)"
            : string.Join(", ", request.TargetCidrs);
        var confirmation = MessageBox.Show(
            this,
            $"Viewer: {request.ViewerIpv4}/32\n스위치 관리망: {networks}\n\n" +
            "기존의 다른 방화벽 규칙은 변경하지 않습니다.\n" +
            "Agent 원격 업무 API가 입력한 Viewer IP만 허용하도록 설정합니다.\n\n" +
            "Agent 서비스를 설치하거나 업데이트하시겠습니까?",
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
    }

    private SetupRequest CreateRequest() =>
        new(
            ViewerIpTextBox.Text.Trim(),
            _networks
                .Where(item => item.IsSelected)
                .Select(item => item.Cidr)
                .Distinct(StringComparer.Ordinal)
                .ToArray());

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
        ViewerIpTextBox.IsEnabled = !busy;
        UseThisPcAddressButton.IsEnabled = !busy;
        ViewerAddressCandidatesComboBox.IsEnabled = !busy;
        RefreshNetworksButton.IsEnabled = !busy;
        NetworkItemsControl.IsEnabled = !busy;
        ManualCidrTextBox.IsEnabled = !busy;
        AddManualNetworkButton.IsEnabled = !busy;
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
        OperationStateText.Text = "취소 및 안전 복구 중";
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
