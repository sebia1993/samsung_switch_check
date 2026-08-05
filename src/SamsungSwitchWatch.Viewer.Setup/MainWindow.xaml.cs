using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using SamsungSwitchWatch.Viewer.Setup.Deployment;

namespace SamsungSwitchWatch.Viewer.Setup;

public partial class MainWindow : Window
{
    private readonly ViewerDeploymentOrchestrator _orchestrator;
    private CancellationTokenSource? _operationCancellation;
    private bool _busy;

    public MainWindow(ViewerDeploymentOrchestrator orchestrator)
    {
        InitializeComponent();
        _orchestrator = orchestrator;
        Loaded += (_, _) => RefreshRecoveryState();
        Closing += OnClosing;
    }

    private async void InstallButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(
            ViewerSetupOperationKind.Install,
            "Viewer를 설치하고 있습니다...",
            token => _orchestrator.DeployAsync(token));
    }

    private async void RecoverButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync(
            ViewerSetupOperationKind.Recovery,
            "이전 Viewer 상태를 복구하고 있습니다...",
            token => _orchestrator.RecoverAsync(token));
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private async Task RunOperationAsync(
        ViewerSetupOperationKind operationKind,
        string progressMessage,
        Func<CancellationToken, Task<ViewerSetupResult>> operation)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _operationCancellation = new CancellationTokenSource();
        SetButtonsEnabled(false);
        SetStatus("작업 중", progressMessage, "", "#2563EB");
        StepList.ItemsSource = null;
        try
        {
            var result = await Task.Run(
                () => operation(_operationCancellation.Token));
            StepList.ItemsSource = result.Steps;
            var presentation = ViewerSetupUiPolicy.Result(operationKind, result);
            SetStatus(
                presentation.Title,
                presentation.Message,
                result.Succeeded ? string.Empty : $"Cause: {result.Code}",
                result.Succeeded ? "#16A34A" : "#DC2626");
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            _busy = false;
            RefreshRecoveryState(updateStatus: false);
            SetButtonsEnabled(true);
        }
    }

    private void RefreshRecoveryState(bool updateStatus = true)
    {
        var recovery = _orchestrator.InspectPendingRecovery();
        var state = ViewerSetupUiPolicy.Buttons(_busy, recovery);
        InstallButton.IsEnabled = state.InstallEnabled;
        RecoverButton.IsEnabled = state.RecoverEnabled;
        CloseButton.IsEnabled = state.CloseEnabled;
        if (updateStatus && recovery.Exists)
        {
            SetStatus(
                "이전 작업 확인 필요",
                recovery.Message,
                $"Cause: {recovery.Code}",
                "#D97706");
        }
    }

    private void SetButtonsEnabled(bool enabled)
    {
        var recovery = _orchestrator.InspectPendingRecovery();
        var state = ViewerSetupUiPolicy.Buttons(!enabled, recovery);
        InstallButton.IsEnabled = state.InstallEnabled;
        CloseButton.IsEnabled = state.CloseEnabled;
        RecoverButton.IsEnabled = state.RecoverEnabled;
    }

    private void SetStatus(
        string title,
        string message,
        string code,
        string color)
    {
        StatusTitle.Text = title;
        StatusMessage.Text = message;
        StatusCode.Text = code;
        StatusCode.Visibility = string.IsNullOrWhiteSpace(code)
            ? Visibility.Collapsed
            : Visibility.Visible;
        StatusIndicator.Fill =
            (SolidColorBrush)new BrushConverter().ConvertFromString(color)!;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_busy)
        {
            return;
        }

        e.Cancel = true;
        _operationCancellation?.Cancel();
    }
}
