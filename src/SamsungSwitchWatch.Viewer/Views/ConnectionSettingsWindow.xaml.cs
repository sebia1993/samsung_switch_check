using System.Windows;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Views;

public partial class ConnectionSettingsWindow : Window
{
    private readonly ViewerSettings _original;
    private readonly Func<ViewerSettings, CancellationToken, Task> _applySettingsAsync;
    private readonly CancellationTokenSource _lifetime = new();
    private ViewerSettings? _identityMismatchCandidate;

    public ConnectionSettingsWindow(
        ViewerSettings settings,
        Func<ViewerSettings, CancellationToken, Task> applySettingsAsync)
    {
        InitializeComponent();
        _original = ViewerSettingsSanitizer.Copy(settings);
        _applySettingsAsync = applySettingsAsync;
        DemoModeCheckBox.IsChecked = settings.DemoMode;
        ViewerSettingsSanitizer.SplitAgentUri(settings.AgentUri, out var address, out var port);
        AgentAddressTextBox.Text = address;
        StartMinimizedCheckBox.IsChecked = settings.StartMinimizedToTray;
        Loaded += (_, _) =>
        {
            FitToWorkingArea();
            if (DemoModeCheckBox.IsChecked == true) DemoModeCheckBox.Focus();
            else AgentAddressTextBox.Focus();
        };
        Closed += (_, _) => _lifetime.Cancel();
        UpdateLiveControls();
    }

    public ViewerSettings? Result { get; private set; }

    private void FitToWorkingArea()
    {
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32);
        Height = Math.Min(Height, MaxHeight);
    }

    private void DemoMode_Changed(object sender, RoutedEventArgs e) => UpdateLiveControls();

    private void UpdateLiveControls() => LiveSettingsPanel.IsEnabled = DemoModeCheckBox.IsChecked != true;

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        var candidate = ViewerSettingsSanitizer.Copy(_original);
        candidate.StartMinimizedToTray = StartMinimizedCheckBox.IsChecked == true;

        if (DemoModeCheckBox.IsChecked == true)
        {
            candidate.DemoMode = true;
            await ApplyAndCloseAsync(ViewerSettingsSanitizer.Sanitize(candidate));
            return;
        }

        if (!ViewerSettingsSanitizer.TryBuildAgentUri(
                AgentAddressTextBox.Text,
                ViewerSettingsSanitizer.DefaultAgentPort.ToString(),
                out var agentUri,
                out var reason))
        {
            ValidationText.Text = reason;
            return;
        }

        candidate.DemoMode = false;
        candidate.AgentUri = agentUri;
        var clean = ViewerSettingsSanitizer.Sanitize(candidate);
        if (!ViewerSettingsSanitizer.IsValidForLiveConnection(clean, out reason))
        {
            ValidationText.Text = reason;
            return;
        }

        await ApplyAndCloseAsync(clean);
    }

    private async Task ApplyAndCloseAsync(ViewerSettings settings)
    {
        SetBusy(true);
        try
        {
            await _applySettingsAsync(settings, _lifetime.Token);
            Result = settings;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            // Application shutdown or an explicit dialog close cancels the operation.
        }
        catch (AgentClientException exception)
        {
            ValidationText.Text = $"{ViewerConnectionMessages.ForCode(exception.ErrorCode)} ({exception.ErrorCode})";
            if (exception.ErrorCode == "AGENT_IDENTITY_CHANGED")
            {
                _identityMismatchCandidate = settings;
                RetrustButton.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            ValidationText.Text = "연결 설정을 적용하지 못했습니다. Agent 서비스와 네트워크 경로를 확인해 주세요.";
        }
        finally
        {
            if (IsVisible) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        SaveButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        AgentAddressTextBox.IsEnabled = !busy;
        DemoModeCheckBox.IsEnabled = !busy;
        SaveButton.Content = busy ? "연결 확인 중…" : "연결 확인 및 저장";
    }

    private async void Retrust_Click(object sender, RoutedEventArgs e)
    {
        if (_identityMismatchCandidate is null) return;
        if (MessageBox.Show(
                this,
                "Agent PC가 실제로 교체되었거나 Agent가 다시 설치된 경우에만 진행하세요.",
                "이 Agent로 다시 연결",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        var candidate = ViewerSettingsSanitizer.Copy(_identityMismatchCandidate);
        candidate.RemoveAgentTrustPin();
        RetrustButton.Visibility = Visibility.Collapsed;
        _identityMismatchCandidate = null;
        await ApplyAndCloseAsync(candidate);
    }
}
