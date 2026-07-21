using System.Windows;
using SamsungSwitchWatch.Viewer.Services;

namespace SamsungSwitchWatch.Viewer.Views;

public partial class ConnectionSettingsWindow : Window
{
    private readonly ViewerSettings _original;
    private readonly ViewerPairingFlow _pairingFlow;
    private readonly Func<ViewerSettings, CancellationToken, Task> _applySettingsAsync;
    private readonly CancellationTokenSource _lifetime = new();

    public ConnectionSettingsWindow(
        ViewerSettings settings,
        Func<ViewerSettings, CancellationToken, Task> applySettingsAsync)
        : this(settings, new ViewerPairingService(), applySettingsAsync)
    {
    }

    internal ConnectionSettingsWindow(
        ViewerSettings settings,
        ViewerPairingService pairingService,
        Func<ViewerSettings, CancellationToken, Task> applySettingsAsync)
    {
        InitializeComponent();
        _original = ViewerPairingService.CopySettings(settings);
        _pairingFlow = new ViewerPairingFlow(pairingService);
        _applySettingsAsync = applySettingsAsync;
        DemoModeCheckBox.IsChecked = settings.DemoMode;
        AgentUriTextBox.Text = settings.AgentUri;
        FingerprintTextBox.Text = string.Join(Environment.NewLine, settings.AcceptedCertificateFingerprints);
        StartMinimizedCheckBox.IsChecked = settings.StartMinimizedToTray;
        Loaded += (_, _) => FitToWorkingArea();
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

    private void UpdateLiveControls()
    {
        var enabled = DemoModeCheckBox.IsChecked != true;
        LiveSettingsPanel.IsEnabled = enabled;
        AdvancedExpander.IsEnabled = enabled;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Text = string.Empty;
        if (DemoModeCheckBox.IsChecked == true)
        {
            var demo = ViewerPairingService.CopySettings(_original);
            demo.DemoMode = true;
            demo.StartMinimizedToTray = StartMinimizedCheckBox.IsChecked == true;
            await ApplyAndCloseAsync(ViewerSettingsSanitizer.Sanitize(demo));
            return;
        }

        if (!string.IsNullOrWhiteSpace(PairingStringTextBox.Text))
        {
            SetBusy(true);
            try
            {
                var paired = await _pairingFlow.PairAndApplyAsync(
                    PairingStringTextBox.Text,
                    _original,
                    StartMinimizedCheckBox.IsChecked == true,
                    _applySettingsAsync,
                    _lifetime.Token);
                PairingStringTextBox.Clear();
                TokenPasswordBox.Clear();
                Result = paired;
                DialogResult = true;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // The dialog is closing. Do not display a late error.
            }
            catch (ViewerPairingException exception)
            {
                ValidationText.Text = $"{exception.UserMessage} ({exception.ErrorCode})";
            }
            catch (AgentClientException exception)
            {
                ValidationText.Text = $"{ViewerPairingMessages.ForCode(exception.ErrorCode)} ({exception.ErrorCode})";
            }
            catch (InvalidOperationException exception) when (
                string.Equals(exception.Message, "VIEWER_TOKEN_PROTECT_FAILED", StringComparison.Ordinal))
            {
                ValidationText.Text = "Viewer 토큰을 Windows 보호 저장소에 저장하지 못했습니다. 같은 연결 문자열로 다시 시도하세요. (VIEWER_TOKEN_PROTECT_FAILED)";
            }
            catch (OperationCanceledException)
            {
                // Application shutdown or an explicit dialog close cancels the operation.
            }
            catch
            {
                ValidationText.Text = "연결 설정을 적용하지 못했습니다. Agent 서비스와 네트워크 경로를 확인하세요.";
            }
            finally
            {
                if (IsVisible) SetBusy(false);
            }
            return;
        }

        if (!ViewerSettingsSanitizer.TryParseFingerprintInput(
                FingerprintTextBox.Text,
                out var fingerprints,
                out var fingerprintReason))
        {
            AdvancedExpander.IsExpanded = true;
            ValidationText.Text = fingerprintReason;
            return;
        }

        var candidate = ViewerPairingService.CopySettings(_original);
        candidate.DemoMode = false;
        candidate.AgentUri = AgentUriTextBox.Text;
        candidate.CertificateFingerprint = fingerprints.FirstOrDefault() ?? string.Empty;
        candidate.CertificateFingerprints = fingerprints.ToList();
        candidate.BearerToken = string.IsNullOrWhiteSpace(TokenPasswordBox.Password)
            ? _original.BearerToken
            : TokenPasswordBox.Password;
        candidate.StartMinimizedToTray = StartMinimizedCheckBox.IsChecked == true;

        var clean = ViewerSettingsSanitizer.Sanitize(candidate);
        clean.BearerToken = candidate.BearerToken;
        if (!ViewerSettingsSanitizer.IsValidForLiveConnection(clean, out var reason))
        {
            AdvancedExpander.IsExpanded = true;
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
            TokenPasswordBox.Clear();
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            // Application shutdown or an explicit dialog close cancels the operation.
        }
        catch (AgentClientException exception)
        {
            ValidationText.Text = $"{ViewerPairingMessages.ForCode(exception.ErrorCode)} ({exception.ErrorCode})";
        }
        catch
        {
            ValidationText.Text = "연결 설정을 적용하지 못했습니다. Agent 서비스와 네트워크 경로를 확인하세요.";
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
        PairingStringTextBox.IsEnabled = !busy;
        AdvancedExpander.IsEnabled = !busy;
        DemoModeCheckBox.IsEnabled = !busy;
        SaveButton.Content = busy ? "연결 확인 중…" : "연결 및 저장";
    }
}
