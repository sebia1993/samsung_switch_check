using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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
    private bool _suppressNetworkSelectionEvent;
    private bool _initialNetworksApplied;
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

        Loaded += (_, _) => RefreshNetworks();
        Closing += OnClosing;
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

    private void RefreshNetworksButton_Click(object sender, RoutedEventArgs e) =>
        RefreshNetworks();

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

    private async void CheckButton_Click(object sender, RoutedEventArgs e)
    {
        var request = CreateRequest();
        await RunOperationAsync(
            "사전 점검 중",
            cancellationToken => _diagnostics.RunAsync(request, cancellationToken));
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
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

        await RunOperationAsync(
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
    }

    private SetupRequest CreateRequest() =>
        new(
            ViewerIpTextBox.Text.Trim(),
            _networks
                .Where(item => item.IsSelected)
                .Select(item => item.Cidr)
                .Distinct(StringComparer.Ordinal)
                .ToArray());

    private async Task RunOperationAsync(
        string runningText,
        Func<CancellationToken, Task<SetupOperationResult>> operation)
    {
        SetBusy(true);
        _results.Clear();
        OperationStateText.Text = runningText;
        _operationCancellation = new CancellationTokenSource();
        try
        {
            var result = await Task.Run(
                () => operation(_operationCancellation.Token),
                _operationCancellation.Token);
            foreach (var step in result.Steps)
            {
                _results.Add(ResultRow.From(step));
            }

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
        }
        catch
        {
            ShowSingleFailure(
                SetupErrorCodes.Unexpected,
                "작업 실패",
                "화면에서 작업 결과를 처리하지 못했습니다.");
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

    private void ShowSingleFailure(string code, string label, string message)
    {
        _results.Clear();
        _results.Add(ResultRow.From(
            new SetupStepResult(code, label, SetupStepState.Failed, message)));
        OperationStateText.Text = $"실패 · {code}";
        OperationStateText.Foreground = Brushes.Firebrick;
    }

    private void SetBusy(bool busy)
    {
        ViewerIpTextBox.IsEnabled = !busy;
        RefreshNetworksButton.IsEnabled = !busy;
        NetworkItemsControl.IsEnabled = !busy;
        ManualCidrTextBox.IsEnabled = !busy;
        AddManualNetworkButton.IsEnabled = !busy;
        CheckButton.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy && !_diagnosticsOnly;
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
