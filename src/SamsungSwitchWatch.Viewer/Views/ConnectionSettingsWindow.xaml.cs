using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SamsungSwitchWatch.Viewer.Services;
using MediaBrushes = System.Windows.Media.Brushes;

namespace SamsungSwitchWatch.Viewer.Views;

public partial class ConnectionSettingsWindow : Window
{
    private readonly ViewerSettings _original;
    private readonly Func<ViewerSettings, CancellationToken, Task> _applySettingsAsync;
    private readonly IAgentConnectionProbe _connectionProbe;
    private readonly ViewerFieldDiagnosticWriter _fieldDiagnosticWriter = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ViewerFieldDiagnosticSnapshot? _lastFieldDiagnostic;
    private AgentConnectionProbeResult? _lastDiagnosticProbeResult;
    private string _lastDiagnosticMode = "NORMAL";
    private int _lastDiagnosticCandidateCount;
    private bool _addressTextInitialized;
    private bool _settingsApplied;

    public ConnectionSettingsWindow(
        ViewerSettings settings,
        Func<ViewerSettings, CancellationToken, Task> applySettingsAsync)
        : this(settings, applySettingsAsync, new AgentConnectionProbe())
    {
    }

    internal ConnectionSettingsWindow(
        ViewerSettings settings,
        Func<ViewerSettings, CancellationToken, Task> applySettingsAsync,
        IAgentConnectionProbe connectionProbe)
    {
        InitializeComponent();
        _original = ViewerSettingsSanitizer.Copy(settings);
        _applySettingsAsync = applySettingsAsync;
        _connectionProbe = connectionProbe ?? throw new ArgumentNullException(nameof(connectionProbe));
        DemoModeCheckBox.IsChecked = settings.DemoMode;
        ViewerSettingsSanitizer.SplitAgentUri(settings.AgentUri, out var address, out var port);
        AgentAddressTextBox.Text = address;
        _addressTextInitialized = true;
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

    internal ViewerFieldDiagnosticSnapshot? FieldDiagnosticSnapshot => _lastFieldDiagnostic;

    private void FitToWorkingArea()
    {
        MaxHeight = Math.Max(MinHeight, SystemParameters.WorkArea.Height - 32);
        Height = Math.Min(Height, MaxHeight);
    }

    private void DemoMode_Changed(object sender, RoutedEventArgs e)
    {
        UpdateLiveControls();
        if (DemoModeCheckBox.IsChecked == true)
        {
            ResetProbeSteps();
            ConnectionProgressPanel.Visibility = Visibility.Collapsed;
            ClearFieldDiagnostic();
        }
    }

    private void UpdateLiveControls() => LiveSettingsPanel.IsEnabled = DemoModeCheckBox.IsChecked != true;

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidationText.Foreground = MediaBrushes.Firebrick;
        ValidationText.Text = string.Empty;

        if (DemoModeCheckBox.IsChecked == true)
        {
            var demoCandidate = ViewerSettingsSanitizer.Copy(_original);
            demoCandidate.StartMinimizedToTray = StartMinimizedCheckBox.IsChecked == true;
            demoCandidate.DemoMode = true;
            await ApplyAndCloseAsync(
                ViewerSettingsSanitizer.Sanitize(demoCandidate),
                probeConnection: false,
                keepOpenAfterApply: false);
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

        var candidate = ViewerSettingsSanitizer.Copy(_original);
        candidate.StartMinimizedToTray = StartMinimizedCheckBox.IsChecked == true;
        candidate.DemoMode = false;
        candidate.AgentUri = agentUri;
        var clean = ViewerSettingsSanitizer.Sanitize(candidate);
        if (!ViewerSettingsSanitizer.IsValidForLiveConnection(clean, out reason))
        {
            ValidationText.Text = reason;
            return;
        }

        await ApplyAndCloseAsync(
            clean,
            probeConnection: true,
            keepOpenAfterApply: true);
    }

    private void AgentAddress_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_addressTextInitialized)
        {
            return;
        }

        ConnectionProgressPanel.Visibility = Visibility.Collapsed;
        ValidationText.Text = string.Empty;
        ClearFieldDiagnostic();
    }

    private async Task ApplyAndCloseAsync(
        ViewerSettings settings,
        bool probeConnection,
        bool keepOpenAfterApply)
    {
        SetBusy(true);
        string? connectionDetail = null;
        try
        {
            if (probeConnection)
            {
                ClearFieldDiagnostic();
                ResetProbeSteps();
                ConnectionProgressPanel.Visibility = Visibility.Visible;
                var progress = new Progress<AgentConnectionProbeUpdate>(UpdateProbeStep);
                var probeResult = await _connectionProbe.ProbeAsync(
                    settings,
                    progress,
                    _lifetime.Token);
                SetFieldDiagnostic("NORMAL", probeResult, 1);
                if (!probeResult.Succeeded)
                {
                    ShowProbeFailure(probeResult);
                    return;
                }

                connectionDetail = probeResult.Detail;
                var hasCompatibilityWarning = IsCompatibilityWarning(connectionDetail);
                ValidationText.Foreground = hasCompatibilityWarning
                    ? new SolidColorBrush(Color.FromRgb(180, 83, 9))
                    : new SolidColorBrush(Color.FromRgb(22, 101, 52));
                ValidationText.Text = hasCompatibilityWarning
                    ? $"{connectionDetail} · 설정을 저장하고 있습니다."
                    : "Agent 연결 확인 완료 · 설정을 저장하고 있습니다.";
            }

            await _applySettingsAsync(settings, _lifetime.Token);
            Result = settings;
            if (keepOpenAfterApply)
            {
                _settingsApplied = true;
                var hasCompatibilityWarning = IsCompatibilityWarning(connectionDetail);
                ValidationText.Foreground = hasCompatibilityWarning
                    ? new SolidColorBrush(Color.FromRgb(180, 83, 9))
                    : new SolidColorBrush(Color.FromRgb(22, 101, 52));
                ValidationText.Text = hasCompatibilityWarning
                    ? $"연결 및 저장 완료 · {connectionDetail}"
                    : "Agent 연결과 설정 저장이 완료되었습니다.";
            }
            else
            {
                DialogResult = true;
            }
        }
        catch (OperationCanceledException)
        {
            // Application shutdown or an explicit dialog close cancels the operation.
        }
        catch (AgentClientException exception)
        {
            ReplaceSuccessfulDiagnosticWithApplyFailure(exception.ErrorCode);
            ValidationText.Foreground = MediaBrushes.Firebrick;
            ValidationText.Text =
                $"{ViewerConnectionMessages.ForCode(exception.ErrorCode)} ({exception.ErrorCode})";
        }
        catch
        {
            ReplaceSuccessfulDiagnosticWithApplyFailure("VIEWER_UNEXPECTED_ERROR");
            if (probeConnection && _lastFieldDiagnostic is null)
            {
                var result = AgentConnectionProbeResult.Failure(
                    AgentConnectionProbeStage.Address,
                    "VIEWER_UNEXPECTED_ERROR",
                    ViewerConnectionMessages.ForCode("VIEWER_UNEXPECTED_ERROR"));
                SetFieldDiagnostic("NORMAL", result, 1);
            }
            ValidationText.Foreground = MediaBrushes.Firebrick;
            ValidationText.Text =
                "연결 설정을 적용하지 못했습니다. Agent 서비스와 네트워크 경로를 확인해 주세요. "
                + "(VIEWER_UNEXPECTED_ERROR)";
        }
        finally
        {
            if (IsVisible) SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        SaveButton.IsEnabled = !busy && !_settingsApplied;
        CancelButton.IsEnabled = !busy;
        AgentAddressTextBox.IsEnabled = !busy && !_settingsApplied;
        DemoModeCheckBox.IsEnabled = !busy && !_settingsApplied;
        StartMinimizedCheckBox.IsEnabled = !busy && !_settingsApplied;
        DiagnosticSaveButton.IsEnabled = !busy && _lastFieldDiagnostic is not null;
        SaveButton.Content = busy
            ? "연결 확인 중…"
            : _settingsApplied
                ? "저장 완료"
                : "연결 확인 및 저장";
        CancelButton.Content = _settingsApplied ? "닫기" : "취소";
    }

    private async void DiagnosticSave_Click(object sender, RoutedEventArgs e)
    {
        var snapshot = _lastFieldDiagnostic;
        if (snapshot is null)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "익명 Agent 연결 진단 저장",
            FileName = $"ssw-viewer-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
            DefaultExt = ".txt",
            AddExtension = true,
            Filter = "텍스트 파일 (*.txt)|*.txt",
            OverwritePrompt = true
        };

        bool accepted;
        try
        {
            accepted = dialog.ShowDialog(this) == true;
        }
        catch
        {
            ShowDiagnosticWriteFailure();
            return;
        }

        if (!accepted)
        {
            return;
        }

        DiagnosticSaveButton.IsEnabled = false;
        var result = await _fieldDiagnosticWriter.WriteAsync(
            dialog.FileName,
            snapshot,
            _lifetime.Token);
        if (result.Succeeded)
        {
            ValidationText.Foreground = new SolidColorBrush(Color.FromRgb(22, 101, 52));
            ValidationText.Text = "사진 한 장용 익명 진단 TXT를 저장했습니다.";
        }
        else
        {
            ShowDiagnosticWriteFailure();
        }

        if (IsVisible)
        {
            DiagnosticSaveButton.IsEnabled = _lastFieldDiagnostic is not null;
        }
    }

    private void SetFieldDiagnostic(
        string mode,
        AgentConnectionProbeResult result,
        int candidateCount)
    {
        _lastFieldDiagnostic = ViewerFieldDiagnostic.Create(
            mode,
            result,
            candidateCount);
        _lastDiagnosticProbeResult = result;
        _lastDiagnosticMode = mode;
        _lastDiagnosticCandidateCount = candidateCount;
        DiagnosticSaveButton.Visibility = Visibility.Visible;
        DiagnosticSaveButton.IsEnabled = true;
        RefreshSupportCode();
    }

    private void ReplaceSuccessfulDiagnosticWithApplyFailure(string errorCode)
    {
        if (_lastFieldDiagnostic?.Result != "SUCCESS"
            || _lastDiagnosticProbeResult is null)
        {
            return;
        }

        _lastFieldDiagnostic = ViewerFieldDiagnostic.CreateApplyFailure(
            _lastDiagnosticMode,
            _lastDiagnosticProbeResult,
            _lastDiagnosticCandidateCount,
            errorCode);
        DiagnosticSaveButton.Visibility = Visibility.Visible;
        DiagnosticSaveButton.IsEnabled = true;
        RefreshSupportCode();
    }

    private void ClearFieldDiagnostic()
    {
        _lastFieldDiagnostic = null;
        _lastDiagnosticProbeResult = null;
        _lastDiagnosticMode = "NORMAL";
        _lastDiagnosticCandidateCount = 0;
        DiagnosticSaveButton.Visibility = Visibility.Collapsed;
        DiagnosticSaveButton.IsEnabled = false;
        HideSupportCode();
    }

    private void RefreshSupportCode()
    {
        HideSupportCode();
        if (_lastFieldDiagnostic is not { Result: "FAILED" } snapshot)
        {
            return;
        }

        try
        {
            SupportCodeTextBox.Text =
                ViewerFieldDiagnostic.CreateSupportCode(snapshot);
            SupportCodePanel.Visibility = Visibility.Visible;
        }
        catch
        {
            HideSupportCode();
        }
    }

    private void HideSupportCode()
    {
        SupportCodeTextBox.Text = string.Empty;
        SupportCodePanel.Visibility = Visibility.Collapsed;
    }

    private void ShowDiagnosticWriteFailure()
    {
        ValidationText.Foreground = MediaBrushes.Firebrick;
        ValidationText.Text =
            "익명 진단을 저장하지 못했습니다. 저장 위치의 쓰기 권한과 디스크 여유 공간을 확인해 주세요. "
            + "(DIAGNOSTIC_WRITE_FAILED)";
    }

    private void ShowProbeFailure(AgentConnectionProbeResult result)
    {
        ValidationText.Foreground = MediaBrushes.Firebrick;
        ValidationText.Text =
            $"{ProbeStageTitle(result.FailedStage)} 단계 실패 · {result.Detail} ({result.ErrorCode})";
    }

    private void ResetProbeSteps()
    {
        ConnectionProgressTitleText.Text = "연결 확인 단계";
        SetProbeText(AddressProbeText, "○", "1. 주소 형식", string.Empty, MediaBrushes.SlateGray);
        SetProbeText(DnsProbeText, "○", "2. DNS 또는 IPv4", string.Empty, MediaBrushes.SlateGray);
        SetProbeText(TcpProbeText, "○", "3. TCP/18443", string.Empty, MediaBrushes.SlateGray);
        SetProbeText(HttpsProbeText, "○", "4. HTTPS 보호", string.Empty, MediaBrushes.SlateGray);
        SetProbeText(IdentityProbeText, "○", "5. Agent API와 버전", string.Empty, MediaBrushes.SlateGray);
        ValidationText.Text = string.Empty;
    }

    private static bool IsCompatibilityWarning(string? detail) =>
        detail?.StartsWith("경고", StringComparison.Ordinal) == true;

    private void UpdateProbeStep(AgentConnectionProbeUpdate update)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !_lifetime.IsCancellationRequested)
            {
                _ = Dispatcher.BeginInvoke(() => UpdateProbeStep(update));
            }
            return;
        }
        if (_lifetime.IsCancellationRequested)
        {
            return;
        }

        var block = update.Stage switch
        {
            AgentConnectionProbeStage.Address => AddressProbeText,
            AgentConnectionProbeStage.Dns => DnsProbeText,
            AgentConnectionProbeStage.Tcp => TcpProbeText,
            AgentConnectionProbeStage.Https => HttpsProbeText,
            AgentConnectionProbeStage.Identity => IdentityProbeText,
            _ => throw new ArgumentOutOfRangeException(nameof(update))
        };
        var (icon, brush) = update.State switch
        {
            AgentConnectionProbeState.Running => ("●", new SolidColorBrush(Color.FromRgb(37, 99, 235))),
            AgentConnectionProbeState.Succeeded => ("✓", new SolidColorBrush(Color.FromRgb(22, 101, 52))),
            AgentConnectionProbeState.Failed => ("!", MediaBrushes.Firebrick),
            _ => ("○", MediaBrushes.SlateGray)
        };
        SetProbeText(block, icon, ProbeStageNumberedTitle(update.Stage), update.Detail, brush);
    }

    private static void SetProbeText(
        TextBlock block,
        string icon,
        string title,
        string detail,
        System.Windows.Media.Brush foreground)
    {
        block.Foreground = foreground;
        block.Text = string.IsNullOrWhiteSpace(detail)
            ? $"{icon} {title}"
            : $"{icon} {title} · {detail}";
    }

    private static string ProbeStageNumberedTitle(AgentConnectionProbeStage stage) => stage switch
    {
        AgentConnectionProbeStage.Address => "1. 주소 형식",
        AgentConnectionProbeStage.Dns => "2. DNS 또는 IPv4",
        AgentConnectionProbeStage.Tcp => "3. TCP/18443",
        AgentConnectionProbeStage.Https => "4. HTTPS 보호",
        AgentConnectionProbeStage.Identity => "5. Agent API와 버전",
        _ => "연결 확인"
    };

    private static string ProbeStageTitle(AgentConnectionProbeStage? stage) => stage switch
    {
        AgentConnectionProbeStage.Address => "주소",
        AgentConnectionProbeStage.Dns => "DNS/IPv4",
        AgentConnectionProbeStage.Tcp => "TCP/18443",
        AgentConnectionProbeStage.Https => "HTTPS",
        AgentConnectionProbeStage.Identity => "Agent API/버전",
        _ => "연결 확인"
    };
}
