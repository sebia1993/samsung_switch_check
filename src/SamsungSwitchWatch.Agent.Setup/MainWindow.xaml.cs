using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
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
    private bool _suppressNetworkSelectionEvent;
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

    private void RefreshNetworksButton_Click(object sender, RoutedEventArgs e) =>
        RefreshNetworks();

    private void RefreshNetworks()
    {
        _networks.Clear();
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

        foreach (var candidate in candidates)
        {
            _networks.Add(new NetworkSelectionItem(candidate)
            {
                IsSelected = candidates.Count == 1
            });
        }

        NoNetworksText.Visibility =
            _networks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NetworkSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressNetworkSelectionEvent)
        {
            return;
        }

        var selected = _networks.Count(item => item.IsSelected);
        if (selected <= 2)
        {
            return;
        }

        _suppressNetworkSelectionEvent = true;
        try
        {
            if (sender is FrameworkElement { DataContext: NetworkSelectionItem item })
            {
                item.IsSelected = false;
            }
        }
        finally
        {
            _suppressNetworkSelectionEvent = false;
        }

        MessageBox.Show(
            this,
            "관리망은 최대 두 개까지 선택할 수 있습니다.",
            "관리망 선택",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
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

            OperationStateText.Text = result.Succeeded
                ? "완료"
                : $"실패 · {result.Code}";
            OperationStateText.Foreground = result.Succeeded
                ? Brushes.SeaGreen
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

public sealed class NetworkSelectionItem(NetworkCandidate candidate) : INotifyPropertyChanged
{
    private bool _isSelected;

    public string InterfaceName { get; } = candidate.InterfaceName;
    public string Address { get; } = candidate.Address;
    public string Cidr { get; } = candidate.Cidr;
    public string Description { get; } = candidate.Description;

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
                _ => "●"
            },
            step.State switch
            {
                SetupStepState.Succeeded => Brushes.SeaGreen,
                SetupStepState.Failed => Brushes.Firebrick,
                SetupStepState.Running => Brushes.RoyalBlue,
                _ => Brushes.DarkGoldenrod
            },
            step.Label,
            step.Message,
            step.Code);
}
